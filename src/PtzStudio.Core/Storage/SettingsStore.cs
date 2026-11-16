using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using PtzStudio.Core.Models;

namespace PtzStudio.Core.Storage;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private bool _blockSave;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "settings.json");
    public string BackupDirectory => Path.Combine(Directory, "backups");

    /// <summary>Aviso da última leitura (arquivo principal ilegível, cópia de segurança usada, arquivo preservado...). Nulo se tudo correu bem.</summary>
    public string? LastLoadWarning { get; private set; }

    public SettingsStore(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public static string DefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PtzStudio");

    /// <summary>
    /// Lê as configurações. Arquivo bloqueado por outro programa (antivírus, nuvem) é tentado de novo; arquivo com conteúdo inválido
    /// cai para a cópia .bak. Se nada puder ser lido, os padrões são usados, mas o arquivo original NUNCA é perdido em silêncio:
    /// ele é copiado para settings.corrompido-DATA.json e, se nem isso for possível, a gravação fica bloqueada nesta sessão.
    /// </summary>
    public AppSettings Load()
    {
        LastLoadWarning = null;
        _blockSave = false;
        bool anyExisted = false;
        foreach (var path in new[] { FilePath, FilePath + ".bak" })
        {
            if (!File.Exists(path)) continue;
            anyExisted = true;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options);
                    if (s != null)
                    {
                        if (path != FilePath)
                            LastLoadWarning = "O arquivo de configurações não pôde ser lido; foi usada a cópia de segurança (.bak). Confira seus perfis e mapeamentos.";
                        return Normalize(s);
                    }
                    break;
                }
                catch (JsonException) { break; }                      // conteúdo inválido: repetir não ajuda
                catch (IOException) { Thread.Sleep(150); }            // arquivo bloqueado por outro programa: tenta de novo
                catch (UnauthorizedAccessException) { Thread.Sleep(150); }
            }
        }

        if (anyExisted && File.Exists(FilePath))
        {
            string keep = Path.Combine(Directory, $"settings.corrompido-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            try
            {
                File.Copy(FilePath, keep, true);
                LastLoadWarning = $"Não foi possível ler as configurações; o programa abriu com os padrões. O arquivo anterior foi guardado em {keep}.";
            }
            catch
            {
                _blockSave = true;
                LastLoadWarning = "Não foi possível ler nem guardar uma cópia das configurações. Para não perdê-las, o programa não vai gravar nada nesta sessão: feche-o, resolva o bloqueio do arquivo e abra de novo.";
            }
        }
        return AppSettings.CreateDefault();
    }

    public void Save(AppSettings s)
    {
        if (_blockSave)
            throw new IOException("A configuração anterior não pôde ser lida nem guardada; a gravação foi bloqueada para não perdê-la.");
        Normalize(s);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(s, Options));
        if (File.Exists(FilePath))
        {
            try { File.Replace(tmp, FilePath, FilePath + ".bak"); }
            catch (IOException)
            {
                File.Copy(FilePath, FilePath + ".bak", true);
                File.Move(tmp, FilePath, true);
            }
        }
        else File.Move(tmp, FilePath, true);
    }

    public static AppSettings Normalize(AppSettings s)
    {
        if (s.Cameras.Count == 0) s.Cameras.Add(new CameraConfig());
        if (!s.Cameras.Any(c => c.Id == s.ActiveCameraId)) s.ActiveCameraId = s.Cameras[0].Id;
        if (s.Profiles.Count == 0) s.Profiles.AddRange(AppSettings.CreateDefault().Profiles);
        if (!s.Profiles.Any(p => p.Name == s.ActiveProfile)) s.ActiveProfile = s.Profiles[0].Name;
        foreach (var kv in AppSettings.DefaultLayout())
            if (!s.Layout.ContainsKey(kv.Key)) s.Layout[kv.Key] = kv.Value;
        foreach (var p in s.Profiles) MigrateProfile(p);
        SettingsSanitizer.Apply(s);   // só corrige valores impossíveis (NaN, fora da faixa); ajustes válidos do usuário ficam como estão
        return s;
    }

    /// <summary>
    /// Perfis de versões antigas: o nível de velocidade padrão (35/65/100%) vira a porcentagem do novo controle contínuo, e as
    /// atribuições de botões que não existem mais (parada de emergência, alternar nível, alternar joystick físico/virtual) são
    /// removidas. Nada mais é alterado.
    /// </summary>
    public static void MigrateProfile(Profile p)
    {
        var m = p.Motion;
        if (m.SpeedLevels is { Length: > 0 } lv)
        {
            int idx = Math.Clamp(m.DefaultSpeedLevel ?? 1, 0, lv.Length - 1);
            m.PanTiltSpeedPercent = (int)Math.Round(lv[idx] * 100);
        }
        m.SpeedLevels = null; m.DefaultSpeedLevel = null;
        p.Bindings.RemoveAll(b => b.Action == ButtonAction.ParadaEmergencia || b.Action == ButtonAction.AlternarVelocidade
                                  || b.Action == ButtonAction.AlternarEntrada);
    }

    // ---- Perfis: exportação e importação ----
    public void ExportProfile(Profile p, string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(p, Options));

    public Profile ImportProfile(string path, AppSettings into)
    {
        var p = JsonSerializer.Deserialize<Profile>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Arquivo de perfil inválido.");
        string baseName = string.IsNullOrWhiteSpace(p.Name) ? "Importado" : p.Name;
        string name = baseName; int n = 2;
        while (into.Profiles.Any(x => x.Name == name)) name = $"{baseName} ({n++})";
        p.Name = name;
        MigrateProfile(p);
        SettingsSanitizer.Motion(p.Motion);
        into.Profiles.Add(p);
        return p;
    }

    // ---- Backup e restauração ----
    /// <summary>Verdadeiro se alguma câmera tem senha de vídeo (RTSP) guardada.</summary>
    public static bool HasPasswords(AppSettings s) => s.Cameras.Any(c => !string.IsNullOrEmpty(c.RtspPassword));

    /// <summary>Cópia das configurações SEM as senhas de vídeo (para enviar o arquivo a outra pessoa sem vazar a senha da câmera).</summary>
    public static AppSettings WithoutPasswords(AppSettings s)
    {
        var copy = JsonClone.Clone(s);
        foreach (var c in copy.Cameras) c.RtspPassword = "";
        return copy;
    }

    /// <param name="includePasswords">Falso: a senha do vídeo (RTSP) é removida do arquivo gerado (as configurações em uso não mudam).</param>
    public string CreateBackup(string? zipPath = null, bool includePasswords = true)
    {
        System.IO.Directory.CreateDirectory(BackupDirectory);
        zipPath ??= Path.Combine(BackupDirectory, $"backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        if (File.Exists(zipPath)) File.Delete(zipPath);
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        if (File.Exists(FilePath))
        {
            if (includePasswords) zip.CreateEntryFromFile(FilePath, "settings.json");
            else
            {
                var current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options);
                var entry = zip.CreateEntry("settings.json");
                using var w = new StreamWriter(entry.Open());
                w.Write(JsonSerializer.Serialize(current == null ? AppSettings.CreateDefault() : WithoutPasswords(current), Options));
            }
        }
        return zipPath;
    }

    /// <summary>Backup automático ao iniciar; mantém apenas os 10 mais recentes.</summary>
    public void AutoBackup()
    {
        if (!File.Exists(FilePath)) return;
        CreateBackup();
        var old = new DirectoryInfo(BackupDirectory).GetFiles("backup-*.zip").OrderByDescending(f => f.CreationTimeUtc).Skip(10);
        foreach (var f in old) { try { f.Delete(); } catch { } }
    }

    public AppSettings Restore(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry("settings.json") ?? throw new InvalidDataException("O backup não contém settings.json.");
        using var rd = new StreamReader(entry.Open());
        var s = JsonSerializer.Deserialize<AppSettings>(rd.ReadToEnd(), Options)
                ?? throw new InvalidDataException("Backup inválido.");
        Normalize(s);
        _blockSave = false;     // restaurar é uma escolha explícita do usuário de substituir o arquivo
        if (File.Exists(FilePath)) File.Copy(FilePath, Path.Combine(Directory, "settings.antes-da-restauracao.json"), true);
        Save(s);
        return s;
    }

    public void ExportAll(AppSettings s, string path, bool includePasswords = true) =>
        File.WriteAllText(path, JsonSerializer.Serialize(includePasswords ? s : WithoutPasswords(s), Options));

    public AppSettings ImportAll(string path)
    {
        var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                ?? throw new InvalidDataException("Arquivo inválido.");
        return Normalize(s);
    }
}
