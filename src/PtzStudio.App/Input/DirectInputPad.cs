using SharpDX.DirectInput;
using PtzStudio.Core.Input;

namespace PtzStudio.App.Input;

/// <summary>
/// Leitura do controle via DirectInput em modo Background: continua funcionando quando o OBS (ou outro
/// programa) está em primeiro plano. Expõe todos os botões, eixos e o direcional que o Windows enxerga.
/// Confere a cada segundo se o controle continua conectado e prefere sempre o GameSir quando há mais de um dispositivo.
/// </summary>
public sealed class DirectInputPad : IDisposable
{
    private readonly DirectInput _di = new();
    private Joystick? _js;
    private IntPtr _hwnd;
    private DateTime _lastScan = DateTime.MinValue;
    private DateTime _lastAttachCheck = DateTime.MinValue;
    private DateTime _lastPreferScan = DateTime.MinValue;
    private Guid? _currentGuid;

    public string? Name { get; private set; }
    public int Vid { get; private set; }
    public int Pid { get; private set; }
    public int ButtonCount { get; private set; }
    public int AxisCount { get; private set; }
    public int PovCount { get; private set; }
    public bool Connected => _js != null;
    /// <summary>Identificação do controle conectado (VID:PID), usada para saber se o mapeamento de botões vale para ele. Vazio se não há controle.</summary>
    public string DeviceKey => Connected ? $"{Vid:X4}:{Pid:X4}" : "";
    public Guid? PreferredId { get; set; }
    public event Action<bool>? ConnectionChanged;

    public sealed record DeviceInfo(Guid Id, string Name, int Vid, int Pid);

    public void Initialize(IntPtr hwnd) { _hwnd = hwnd; Scan(); }   // a busca roda em segundo plano: não atrasa a abertura

    private static bool IsGameSir(string? name) => name != null && name.Contains("GameSir", StringComparison.OrdinalIgnoreCase);

    private static List<DeviceInfo> Enumerate(DirectInput di)
    {
        var list = new List<DeviceInfo>();
        try
        {
            foreach (var d in di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AttachedOnly))
            {
                var b = d.ProductGuid.ToByteArray();
                list.Add(new DeviceInfo(d.InstanceGuid, d.InstanceName, b[0] | (b[1] << 8), b[2] | (b[3] << 8))); // VID = 2 bytes baixos do ProductGuid, PID = 2 altos
            }
        }
        catch { }
        return list;
    }

    public List<DeviceInfo> ListDevices() => Enumerate(_di);

    private volatile bool _scanning;
    private List<DeviceInfo>? _scanResult;   // preenchido pela tarefa de fundo, consumido na thread da tela
    private volatile bool _preferScanning;
    private List<DeviceInfo>? _preferResult;

    /// <summary>
    /// Procura o controle. A primeira busca (force) é imediata; as seguintes rodam em segundo plano (a enumeração do
    /// DirectInput pode demorar e não deve travar a tela nem o envio de comandos). O resultado é aplicado no próximo ciclo.
    /// </summary>
    public void Scan(bool force = false)
    {
        if (force) { _lastScan = DateTime.UtcNow; Apply(Enumerate(_di)); return; }
        var ready = Interlocked.Exchange(ref _scanResult, null);
        if (ready != null) Apply(ready);
        if (_scanning || (DateTime.UtcNow - _lastScan).TotalSeconds < 2) return;
        _lastScan = DateTime.UtcNow;
        _scanning = true;
        Task.Run(() =>
        {
            try
            {
                using var di = new DirectInput();    // instância própria: não compartilha COM com a thread da tela
                _scanResult = Enumerate(di);
            }
            catch { }
            finally { _scanning = false; }
        });
    }

    private void Apply(List<DeviceInfo> devs)
    {
        if (_js != null)
        {
            if (devs.Any(d => d.Id == _currentGuid)) return;
            Release();
        }
        if (devs.Count == 0) return;
        var pick = devs.FirstOrDefault(d => PreferredId != null && d.Id == PreferredId)
                   ?? devs.FirstOrDefault(d => IsGameSir(d.Name))
                   ?? devs.First();
        try
        {
            var js = new Joystick(_di, pick.Id);
            js.SetCooperativeLevel(_hwnd, CooperativeLevel.Background | CooperativeLevel.NonExclusive);
            js.Properties.BufferSize = 128;
            js.Acquire();
            _js = js; _currentGuid = pick.Id;
            Name = pick.Name; Vid = pick.Vid; Pid = pick.Pid;
            ButtonCount = js.Capabilities.ButtonCount;
            AxisCount = js.Capabilities.AxeCount;
            PovCount = js.Capabilities.PovCount;
            ConnectionChanged?.Invoke(true);
        }
        catch
        {
            _js = null;
        }
    }

    /// <summary>
    /// A cada segundo, com o controle conectado: (1) confirma que o dispositivo ainda está no Windows (um Bluetooth que cai sem
    /// erro deixaria o último estado do analógico "preso"); (2) se o dispositivo em uso não é o GameSir e um GameSir aparecer, troca.
    /// Devolve falso se o dispositivo mudou ou sumiu (a leitura deste ciclo deve ser descartada).
    /// </summary>
    private bool CheckAttachedAndPreferred()
    {
        _lastAttachCheck = DateTime.UtcNow;
        var before = _js;
        try
        {
            if (_currentGuid is Guid g && !_di.IsDeviceAttached(g)) { Release(); return false; }
        }
        catch { }

        var ready = Interlocked.Exchange(ref _preferResult, null);
        if (ready != null && !IsGameSir(Name))
        {
            var gs = ready.FirstOrDefault(d => IsGameSir(d.Name));
            if (gs != null) { PreferredId = gs.Id; Release(); Apply(ready); return false; }
        }
        if (!IsGameSir(Name) && !_preferScanning && (DateTime.UtcNow - _lastPreferScan).TotalSeconds >= 3)
        {
            _lastPreferScan = DateTime.UtcNow;
            _preferScanning = true;
            Task.Run(() =>
            {
                try
                {
                    using var di = new DirectInput();
                    _preferResult = Enumerate(di);
                }
                catch { }
                finally { _preferScanning = false; }
            });
        }
        return ReferenceEquals(before, _js);
    }

    public InputSnapshot? Poll()
    {
        if (_js == null) { Scan(); return null; }
        if ((DateTime.UtcNow - _lastAttachCheck).TotalSeconds >= 1 && !CheckAttachedAndPreferred()) return null;
        try
        {
            _js.Poll();
            var st = _js.GetCurrentState();
            var snap = new InputSnapshot
            {
                Buttons = (bool[])st.Buttons.Clone(),
                Axes = new[]
                {
                    st.X / 65535.0, st.Y / 65535.0, st.Z / 65535.0,
                    st.RotationX / 65535.0, st.RotationY / 65535.0, st.RotationZ / 65535.0,
                    st.Sliders[0] / 65535.0, st.Sliders[1] / 65535.0
                },
                Pov = (int[])st.PointOfViewControllers.Clone()
            };
            return snap;
        }
        catch (Exception)
        {
            // SharpDXException (InputLost/NotAcquired) ou qualquer outra falha: trata como controle desconectado
            Release();
            return null;
        }
    }

    private void Release()
    {
        try { _js?.Unacquire(); _js?.Dispose(); } catch { }
        _js = null; _currentGuid = null;
        ConnectionChanged?.Invoke(false);
    }

    public void Dispose() { try { _js?.Dispose(); _di.Dispose(); } catch { } }
}
