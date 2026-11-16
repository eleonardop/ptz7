using PtzStudio.Core.Models;
using PtzStudio.Core.Storage;
using Xunit;

namespace PtzStudio.Core.Tests;

/// <summary>A configuração nunca é perdida em silêncio, e o mapeamento de botões sabe para qual controle foi feito.</summary>
public class SettingsLoadSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ptz-safe-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void ALockedSettingsFile_IsNeverOverwrittenWithDefaults()
    {
        if (!OperatingSystem.IsWindows()) return;                  // o bloqueio de arquivo por outro programa é típico do Windows
        var st = new SettingsStore(_dir);
        var s = st.Load(); s.Profiles[0].Name = "Igreja"; st.Save(s);

        using (var locked = new FileStream(st.FilePath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var again = new SettingsStore(_dir);
            var fallback = again.Load();                           // não consegue ler nem copiar o arquivo
            Assert.NotNull(again.LastLoadWarning);
            Assert.NotEmpty(fallback.Profiles);
            Assert.Throws<IOException>(() => again.Save(fallback)); // gravar por cima ficaria bloqueado
        }
        Assert.Contains("Igreja", File.ReadAllText(st.FilePath));   // o arquivo original continua intacto
    }

    [Fact]
    public void WhenNothingCanBeRead_TheOriginalFileIsPreserved_BeforeDefaultsAreUsed()
    {
        Directory.CreateDirectory(_dir);
        var st = new SettingsStore(_dir);
        File.WriteAllText(st.FilePath, "conteudo-principal-estragado");
        File.WriteAllText(st.FilePath + ".bak", "conteudo-bak-estragado");

        var loaded = st.Load();
        Assert.NotEmpty(loaded.Profiles);                           // abriu com os padrões...
        Assert.NotNull(st.LastLoadWarning);
        var kept = Directory.GetFiles(_dir, "settings.corrompido-*.json");
        Assert.Single(kept);                                        // ...mas o arquivo anterior ficou guardado
        Assert.Equal("conteudo-principal-estragado", File.ReadAllText(kept[0]));
        st.Save(loaded);                                            // e agora gravar é seguro
        Assert.Contains("Igreja", File.ReadAllText(st.FilePath));
    }

    [Fact]
    public void WhenOnlyTheMainFileIsBad_TheBackupIsUsed_WithAWarning()
    {
        var st = new SettingsStore(_dir);
        var s = st.Load(); s.Cameras[0].Name = "Camera do altar"; st.Save(s);
        st.Save(s);                                                 // cria o .bak
        File.WriteAllText(st.FilePath, "{ quebrado");

        var again = new SettingsStore(_dir);
        var r = again.Load();
        Assert.Equal("Camera do altar", r.Cameras[0].Name);
        Assert.NotNull(again.LastLoadWarning);
        Assert.Contains(".bak", again.LastLoadWarning);
    }

    [Fact]
    public void AHealthyLoad_HasNoWarning_AndSavingKeepsABackup()
    {
        var st = new SettingsStore(_dir);
        st.Save(st.Load());
        st.Save(st.Load());
        var again = new SettingsStore(_dir);
        again.Load();
        Assert.Null(again.LastLoadWarning);
        Assert.True(File.Exists(st.FilePath + ".bak"));
    }

    // ---------------- mapeamento x controle conectado ----------------
    [Fact]
    public void LayoutDeviceCheck_ComparesTheConnectedControllerWithTheMappedOne()
    {
        var s = AppSettings.CreateDefault();
        Assert.Equal("", s.LayoutDevice);
        Assert.Equal(LayoutDeviceCheck.NotRecorded, LayoutMigration.CheckDevice(s, "045E:028E"));   // ainda não registrado
        Assert.Equal(LayoutDeviceCheck.NotRecorded, LayoutMigration.CheckDevice(s, ""));            // sem controle conectado

        s.LayoutDevice = "045E:028E";
        Assert.Equal(LayoutDeviceCheck.Match, LayoutMigration.CheckDevice(s, "045e:028e"));         // maiúsculas não importam
        Assert.Equal(LayoutDeviceCheck.Mismatch, LayoutMigration.CheckDevice(s, "3537:1001"));      // outro modo de conexão
    }

    [Fact]
    public void OldFiles_LoadWithoutALayoutDevice_AndWithSoftwareDecoding()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"ActiveProfile\":\"Igreja\"}")!;
        Assert.Equal("", s.LayoutDevice);
        var c = System.Text.Json.JsonSerializer.Deserialize<CameraConfig>("{\"Name\":\"Antiga\"}")!;
        Assert.False(c.VideoHardwareDecode);
    }
}
