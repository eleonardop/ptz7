using System.IO;
using System.Windows;
using System.Windows.Threading;
using PtzStudio.App.Input;
using PtzStudio.Core;
using PtzStudio.Core.Control;
using PtzStudio.Core.Input;
using PtzStudio.Core.Models;
using PtzStudio.Core.Storage;
using PtzStudio.Core.Visca;

namespace PtzStudio.App.Engine;

/// <summary>Liga entrada (controle/virtual), núcleo de controle, câmera e persistência. Não contém desenho de tela.</summary>
public sealed class AppEngine : IDisposable
{
    /// <summary>Tempo máximo da detecção de botão/eixo: sem ninguém apertar nada, ela se cancela e o controle volta ao normal.</summary>
    public const int DetectTimeoutSeconds = 10;

    public SettingsStore Store { get; }
    public AppSettings Settings { get; set; }
    public ViscaClient Client { get; } = new();
    public PtzController Controller { get; }
    public ButtonRouter Router { get; } = new();
    /// <summary>Brilho e exposição da câmera (interface e botões do controle usam o mesmo objeto, então ficam sincronizados).</summary>
    public CameraImage Image { get; }
    /// <summary>Foco automático (estado lido da câmera). Interface e botões do controle usam o mesmo objeto.</summary>
    public CameraFocus Focus { get; }
    /// <summary>Tempo entre abrir o programa e a primeira tela aparecer (ms). Só para o Diagnóstico.</summary>
    public double StartupMs { get; set; }
    public DirectInputPad Pad { get; } = new();

    public InputSnapshot? LastSnapshot { get; private set; }
    public InterpretedInput Last { get; private set; } = new();
    /// <summary>Posição de repouso dos eixos (gatilhos e botões de eixo). Só é gravada com todos os eixos parados.</summary>
    public double[] Rest { get; private set; } = new double[8];
    /// <summary>Endereço, porta ou protocolo da câmera ativa foram editados e ainda não aplicados (a conexão em uso é a antiga).</summary>
    public bool CameraChangesPending => AppliedCameraSig != CameraSig(Camera);
    public double VirtualX, VirtualY, VirtualZ;

    /// <summary>Aviso de configuração (arquivo ilegível, cópia usada...) mostrado numa faixa fixa até o operador dispensar.</summary>
    public string? StartupWarning { get; set; }
    /// <summary>Verdadeiro quando o controle conectado não é o controle para o qual o mapeamento foi feito: as funções dos botões ficam suspensas.</summary>
    public bool LayoutMismatch { get; private set; }

    public event Action? ProfileChanged;
    public event Action? SettingsReplaced;
    public event Action<string>? Notice;

    // Prioridade Normal: o padrão do DispatcherTimer (Background) perde para mouse e desenho da tela, o que atrasaria comandos e PARADAS.
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(16) };
    private DateTime _lastTick = MonoClock.UtcNow;
    private readonly RestTracker _rest = new();
    private string _restDevice = "";
    private bool _restWarned;
    private volatile CameraConfig _camSnap = new();     // cópia da câmera ativa para as threads de fundo (nunca a lista viva)
    private DateTime _lastTickErr = DateTime.MinValue;
    private int _tickErrCount;
    private Action<int>? _detectButton;
    private Action<int>? _detectAxis;
    private DateTime _detectStartedUtc = DateTime.MinValue;
    private HashSet<int> _detectBaseline = new();
    private double[]? _detectAxisBase;
    private (int slot, DateTime when)? _pendingSave;
    private int _pendingSock = -1;
    private readonly object _issueLock = new();
    // Gravação com atraso: arrastar um slider gera dezenas de alterações por segundo; gravar uma por uma travaria a mesma thread do controle.
    private readonly DispatcherTimer _saveTimer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _dirty;
    private bool _waitRelease;   // detecção acabou de capturar um botão: ignora funções até soltar tudo
    private readonly List<string> _issues = new();
    private ControlGuardian? _guardian;

    public Profile Profile => Settings.Profiles.FirstOrDefault(p => p.Name == Settings.ActiveProfile) ?? Settings.Profiles[0];
    public CameraConfig Camera => Settings.Cameras.FirstOrDefault(c => c.Id == Settings.ActiveCameraId) ?? Settings.Cameras[0];
    public bool Detecting => _detectButton != null || _detectAxis != null;

    /// <summary>Últimos problemas (erros de comunicação e falhas internas), só na memória: no máximo 30. Não há histórico de comandos.</summary>
    public IReadOnlyList<string> RecentIssues { get { lock (_issueLock) return _issues.ToArray(); } }

    public void AddIssue(string text)
    {
        lock (_issueLock)
        {
            _issues.Add($"{DateTime.Now:HH:mm:ss} {text}");
            if (_issues.Count > 30) _issues.RemoveAt(0);
        }
    }

    public AppEngine()
    {
        Store = new SettingsStore(SettingsStore.DefaultDirectory());
        Settings = Store.Load();
        StartupWarning = Store.LastLoadWarning;
        if (StartupWarning != null) AddIssue("Configuração: " + StartupWarning);
        // O backup automático (compacta um arquivo) não precisa atrasar a abertura da janela: roda em segundo plano,
        // depois da leitura (assim ele nunca copia um arquivo que acabou de ser tratado como ilegível).
        var store = Store;
        if (StartupWarning == null) _ = Task.Run(() => { try { store.AutoBackup(); } catch { } });

        RefreshSnapshot();
        AppliedCameraSig = CameraSig(Camera);     // a conexão ainda será aberta em Start(): até lá, nada está "pendente"
        // As threads de fundo (guardião, consultas à câmera) recebem uma CÓPIA da câmera ativa, nunca a lista que a tela edita.
        Controller = new PtzController(Client, () => _camSnap, () => Profile);
        Controller.AttachFeedback(Client);
        CrashGuard.StopAction = () => Controller.ForceStopOutOfBand("encerramento inesperado do programa");
        Image = new CameraImage(Client, () => _camSnap);
        Image.Notice += m => Application.Current?.Dispatcher.BeginInvoke(() => Notice?.Invoke(m));
        Focus = new CameraFocus(Client, () => _camSnap);
        Focus.Notice += m => Application.Current?.Dispatcher.BeginInvoke(() => Notice?.Invoke(m));
        Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
        Controller.Message += m => Notice?.Invoke(m);
        Router.Triggered += Execute;
        Client.ErrorReported += t => AddIssue("Câmera: " + t);
        Client.StateChanged += (st, msg) =>
        {
            if (st == ConnectionState.Desconectado) Controller.SafetyStop("comunicação com a câmera perdida");
            if (st == ConnectionState.Conectado)
            {
                // Câmera (re)conectada: pergunta a ela quais ajustes de imagem suporta (em segundo plano; nada é presumido)
                // e reafirma a parada, caso um pacote de parada tenha se perdido durante a queda (ou o programa tenha caído antes).
                _ = Image.DetectAsync();
                _ = Focus.RefreshAsync();
                Controller.ReassertStopIfIdle("câmera respondendo");
            }
            Application.Current?.Dispatcher.BeginInvoke(() => Notice?.Invoke("Câmera: " + msg));
        };
        Client.ResponseReceived += r =>
        {
            if (_pendingSave is not { } ps || (MonoClock.UtcNow - ps.when).TotalSeconds >= 3) return;
            // Só confirma se a CONCLUSÃO vier do mesmo "socket" VISCA que o ACK do comando de gravar (evita confundir
            // com a conclusão de um zoom/foco qualquer).
            int sock = r.Payload.Length >= 2 ? r.Payload[1] & 0x0F : -1;
            if (r.Kind == ViscaResponseKind.Ack) { if (_pendingSock < 0) _pendingSock = sock; return; }
            if (r.Kind == ViscaResponseKind.Conclusao && _pendingSock >= 0 && sock == _pendingSock)
            {
                _pendingSave = null;
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    var e = Settings.Presets.FirstOrDefault(x => x.CameraId == Camera.Id && x.Slot == ps.slot);
                    if (e != null) { e.ConfirmedOnCamera = true; Save(); Notice?.Invoke($"Preset {ps.slot} confirmado pela câmera."); }
                });
            }
        };
        Pad.ConnectionChanged += c =>
        {
            if (!c) { _rest.Reset(); _restWarned = false; Controller.SafetyStop("controle desconectado"); }
            else
            {
                // Outro controle: o repouso antigo não serve de comparação. O mesmo controle: o repouso novo é conferido com o anterior.
                string key = Pad.DeviceKey;
                if (key != _restDevice) { _rest.ForgetHistory(); _restDevice = key; } else _rest.Reset();
                _restWarned = false;
                CheckLayoutDevice();
            }
            Notice?.Invoke(c ? $"Controle conectado: {Pad.Name}" : "Controle desconectado");
        };
        _saveTimer.Tick += (_, _) => Flush();
        _timer.Tick += (_, _) =>
        {
            try { Tick(); _tickErrCount = 0; }
            catch (Exception ex)
            {
                // Uma falha que se repete a cada ciclo (60 por segundo) não pode virar uma tempestade de paradas e avisos:
                // a primeira para a câmera; depois só se repete a cada 2 s, e o aviso aparece uma vez.
                _tickErrCount++;
                var nowErr = MonoClock.UtcNow;
                if (_tickErrCount == 1 || (nowErr - _lastTickErr).TotalSeconds >= 2)
                {
                    _lastTickErr = nowErr;
                    if (_tickErrCount == 1) AddIssue("Falha no ciclo de controle: " + ex.Message);
                    try { Controller.SafetyStop("erro interno no ciclo de controle"); } catch { }
                }
            }
        };
    }

    /// <summary>Chamado depois que a janela já apareceu. O controle é procurado em segundo plano; o ciclo de comandos só começa com tudo pronto.</summary>
    public void Start(IntPtr hwnd)
    {
        Pad.Initialize(hwnd);
        ConnectCamera();
        _timer.Start();
        _guardian = new ControlGuardian(Controller);   // thread própria: manda parar se o ciclo de controle travar com a câmera em movimento
    }

    /// <summary>Identifica o que define a CONEXÃO com a câmera (o resto, como velocidades, vale na hora).</summary>
    public static string CameraSig(CameraConfig c) => $"{c.Id}|{(c.IpAddress ?? "").Trim()}|{c.Port}|{c.Transport}";

    /// <summary>A conexão em uso foi aberta com este endereço/porta/protocolo.</summary>
    public string AppliedCameraSig { get; private set; } = "";

    private void RefreshSnapshot()
    {
        try { _camSnap = Camera.Clone(); } catch { }
    }

    public void ConnectCamera()
    {
        Image.Reset();
        Focus.Reset();
        RefreshSnapshot();
        AppliedCameraSig = CameraSig(Camera);
        Client.Connect(Camera);
    }

    // ---------- suspensão e retomada do Windows ----------
    private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
    {
        if (e.Mode == Microsoft.Win32.PowerModes.Suspend)
        {
            try { Controller.StopAll("o computador vai suspender"); } catch { }
        }
        else if (e.Mode == Microsoft.Win32.PowerModes.Resume)
        {
            // Depois de acordar, a rede pode levar alguns segundos para voltar: espera um pouco e refaz a conexão e o controle.
            _ = Task.Delay(3000).ContinueWith(_ => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _rest.Reset(); _restWarned = false;
                _lastTick = MonoClock.UtcNow;
                Notice?.Invoke("O computador acordou: reconectando à câmera...");
                ConnectCamera();
            })));
        }
    }

    // ---------- mapeamento x controle conectado ----------
    private void CheckLayoutDevice()
    {
        string key = Pad.DeviceKey;
        switch (LayoutMigration.CheckDevice(Settings, key))
        {
            case LayoutDeviceCheck.NotRecorded:
                if (key.Length > 0) { Settings.LayoutDevice = key; Save(); }   // primeiro controle conectado: assume que o mapeamento é dele
                LayoutMismatch = false;
                break;
            case LayoutDeviceCheck.Match:
                LayoutMismatch = false;
                break;
            case LayoutDeviceCheck.Mismatch:
                LayoutMismatch = true;
                Notice?.Invoke($"Este controle ({key}) é diferente do mapeado ({Settings.LayoutDevice}): as funções dos botões foram suspensas.");
                break;
        }
    }

    /// <summary>O operador confirma que o mapeamento atual vale para o controle conectado (ou refez o mapeamento com ele).</summary>
    public void AcceptCurrentDeviceLayout()
    {
        if (Pad.DeviceKey.Length > 0) Settings.LayoutDevice = Pad.DeviceKey;
        LayoutMismatch = false;
        Save();
        Notice?.Invoke("Mapeamento aceito para o controle conectado.");
    }

    private void Tick()
    {
        var now = MonoClock.UtcNow;
        double dt = Math.Clamp((now - _lastTick).TotalSeconds, 0.001, 0.1);
        _lastTick = now;

        var snap = Pad.Poll();
        if (snap != null)
        {
            // Enquanto o repouso não está gravado (eixos ainda mexendo), nenhum "botão de eixo" pode disparar: o repouso provisório é a leitura atual.
            bool restReady = _rest.Offer(snap.Axes, now);
            Rest = restReady ? _rest.Rest : (double[])snap.Axes.Clone();
            if (restReady && _rest.Warning != null && !_restWarned)
            {
                _restWarned = true;
                AddIssue(_rest.Warning);
                Notice?.Invoke(_rest.Warning);
            }
            LastSnapshot = snap;
            var watched = Settings.Layout.Values.Where(v => v >= 2000).ToList();
            if (_detectButton != null)
                for (int a = 0; a < 8; a++) { watched.Add(VirtualButtons.AxisPlus(a)); watched.Add(VirtualButtons.AxisMinus(a)); }
            Last = InputInterpreter.Interpret(snap, Profile.AxisMap, Rest, watched);
            ProcessDetection(snap);
        }
        else
        {
            LastSnapshot = null;
            Last = new InterpretedInput();
        }

        if (_waitRelease && Last.Pressed.Count == 0) _waitRelease = false;

        // A detecção nunca pode deixar o controle mudo por muito tempo: sem ninguém apertar nada, ela se cancela.
        if (Detecting && (now - _detectStartedUtc).TotalSeconds > DetectTimeoutSeconds)
        {
            CancelDetect();
            Notice?.Invoke($"A detecção foi cancelada: ninguém apertou um botão ou mexeu no analógico em {DetectTimeoutSeconds} s.");
        }

        bool axisDetect = _detectAxis != null;                                  // só a detecção de eixo precisa do analógico parado
        bool buttonsOff = Detecting || _waitRelease || LayoutMismatch;          // funções dos botões suspensas
        if (buttonsOff)
        {
            Router.Update(Array.Empty<int>(), Profile, now);   // não executa funções enquanto detecta (nem com o botão detectado ainda apertado)
            Image.SetHold(ImageParam.Brilho, 0, now); Image.SetHold(ImageParam.Exposicao, 0, now);
            Controller.SetDigital(0, 0, 0, 0);
            Controller.SetTriggerZoom(0);
        }
        else
        {
            Router.Update(Last.Pressed, Profile, now, Last.Analog);
            Image.SetHold(ImageParam.Brilho, Router.HeldBright, now);
            Image.SetHold(ImageParam.Exposicao, Router.HeldExposure, now);
            Controller.SetDigital(Router.HeldPan, Router.HeldTilt, Router.HeldZoom, Router.HeldFocus);
            Controller.SetTriggerZoom(Router.HeldZoomAnalog);
        }
        if (axisDetect) Controller.SetAnalog(InputSource.Fisico, 0, 0, 0, now);
        else Controller.SetAnalog(InputSource.Fisico, Last.X, Last.Y, Last.Z, now);
        Image.Tick(now);
        // O joystick virtual (mouse) e o controle físico funcionam ao mesmo tempo; o PtzController decide quem está no comando.
        Controller.SetAnalog(InputSource.Virtual, VirtualX, VirtualY, VirtualZ, now);
        Controller.Tick(dt, now);
    }

    // ---------- detecção automática ----------
    public void BeginDetectButton(Action<int> done)
    {
        _detectBaseline = new HashSet<int>(Last.Pressed);
        _detectButton = done;
        _detectAxis = null;
        _detectStartedUtc = MonoClock.UtcNow;
    }

    public void BeginDetectAxis(Action<int> done)
    {
        _detectAxisBase = LastSnapshot != null ? (double[])LastSnapshot.Axes.Clone() : null;
        _detectAxis = done;
        _detectButton = null;
        _detectStartedUtc = MonoClock.UtcNow;
    }

    /// <summary>Pedido do operador: grava um repouso novo (aceita qualquer posição, mesmo diferente da anterior).</summary>
    public void RecalibrateRest() { _rest.ForgetHistory(); _restWarned = false; }

    public void CancelDetect() { _detectButton = null; _detectAxis = null; }

    private void ProcessDetection(InputSnapshot snap)
    {
        if (_detectButton != null)
        {
            // sticks atribuídos não contam como "botão"
            var map = Profile.AxisMap;
            var ignore = new HashSet<int> { map.PanAxis, map.TiltAxis, map.ZoomAxis };
            foreach (int id in Last.Pressed)
            {
                if (_detectBaseline.Contains(id)) continue;
                if (id >= 2000 && ignore.Contains(id % 1000)) continue;
                var cb = _detectButton; _detectButton = null;
                _waitRelease = true;
                cb?.Invoke(id);
                return;
            }
            _detectBaseline.IntersectWith(Last.Pressed);
        }
        else if (_detectAxis != null && _detectAxisBase != null)
        {
            for (int a = 0; a < snap.Axes.Length; a++)
            {
                if (Math.Abs(snap.Axes[a] - _detectAxisBase[a]) > 0.4)
                {
                    var cb = _detectAxis; _detectAxis = null;
                    cb?.Invoke(a);
                    return;
                }
            }
        }
        else if (_detectAxis != null) _detectAxisBase = (double[])snap.Axes.Clone();
    }

    // ---------- ações de botões ----------
    public void Execute(ButtonAction a, int p)
    {
        if (a == ButtonAction.Nenhuma) return;
        switch (a)
        {
            case ButtonAction.Parar: Controller.StopAll("botão Parar"); break;
            case ButtonAction.SalvarPreset: SavePresetSlot(p, null); break;
            case ButtonAction.AcionarPreset: if (Controller.RecallPreset(p)) Notice?.Invoke($"Preset {p} acionado"); break;
            case ButtonAction.AlternarPerfil: NextProfile(); break;
            case ButtonAction.Home: Controller.Home(); break;
            case ButtonAction.AutoFoco: _ = ToggleAutoFocusAsync(); break;
            case ButtonAction.FocoUmToque: _ = Focus.OnePushAsync(); break;
        }
    }

    /// <summary>Liga/desliga o autofoco pelo estado REAL da câmera e avisa o resultado (usado pelo botão da tela e pelo do controle).</summary>
    public async Task ToggleAutoFocusAsync()
    {
        try
        {
            var mode = await Focus.ToggleAsync().ConfigureAwait(false);
            string txt = "Autofoco: " + (mode == FocusMode.Automatico ? "LIGADO" : "DESLIGADO (foco manual)") +
                         (Focus.Confirmed ? "" : " — a câmera ainda não confirmou");
            Application.Current?.Dispatcher.BeginInvoke(new Action(() => Notice?.Invoke(txt)));
        }
        catch (Exception ex) { AddIssue("Autofoco: " + ex.Message); }
    }

    public void SavePresetSlot(int slot, string? name)
    {
        if (!Controller.SavePreset(slot)) return;      // número fora da faixa da câmera: nada foi enviado nem registrado
        var e = Settings.Presets.FirstOrDefault(x => x.CameraId == Camera.Id && x.Slot == slot);
        if (e == null) { e = new PresetEntry { CameraId = Camera.Id, Slot = slot, Name = name ?? $"Preset {slot}" }; Settings.Presets.Add(e); }
        else if (name != null) e.Name = name;
        e.ConfirmedOnCamera = false;
        e.SentAt = DateTime.Now;
        _pendingSock = -1;
        _pendingSave = (slot, MonoClock.UtcNow);
        Save();
        Notice?.Invoke($"Comando de gravar preset {slot} enviado; aguardando confirmação da câmera.");
    }

    public void NextProfile()
    {
        int i = Settings.Profiles.FindIndex(p => p.Name == Settings.ActiveProfile);
        SetProfile(Settings.Profiles[(i + 1) % Settings.Profiles.Count].Name);
    }

    public void SetProfile(string name)
    {
        Controller.StopAll("troca de perfil");
        Settings.ActiveProfile = name;
        Save();
        ProfileChanged?.Invoke();
        Notice?.Invoke("Perfil ativo: " + name);
    }

    public void ReplaceSettings(AppSettings s)
    {
        Controller.StopAll("restauração de configurações");
        _saveTimer.Stop(); _dirty = false;   // gravação pendente era das configurações antigas
        Settings = s;
        LayoutMismatch = false;
        if (Pad.Connected) CheckLayoutDevice();
        ConnectCamera();
        SettingsReplaced?.Invoke();
        ProfileChanged?.Invoke();
    }

    /// <summary>Pede para gravar as configurações. A gravação acontece logo depois (agrupa várias alterações seguidas).</summary>
    public void Save()
    {
        if (!_saveTimer.Dispatcher.CheckAccess()) { _saveTimer.Dispatcher.BeginInvoke(new Action(Save)); return; }
        RefreshSnapshot();      // as threads de fundo passam a ver as velocidades, o endereço VISCA e os limites já editados
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Grava agora o que estiver pendente (antes de backup, exportação ou ao sair).</summary>
    public void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty) return;
        _dirty = false;
        try { Store.Save(Settings); }
        catch (Exception e) { Notice?.Invoke("Não foi possível salvar: " + e.Message); }
    }

    /// <summary>Aplica um novo mapeamento (peça do desenho → número do botão) levando junto as funções já atribuídas.</summary>
    public void ApplyLayout(IReadOnlyDictionary<string, int> newLayout)
    {
        LayoutMigration.Apply(Settings, newLayout);
        if (Pad.DeviceKey.Length > 0) Settings.LayoutDevice = Pad.DeviceKey;   // o mapeamento agora vale para o controle conectado
        LayoutMismatch = false;
        Controller.StopAll("novo mapeamento de botões");
        Save();
    }

    /// <summary>Volta os ajustes de movimento do perfil ativo aos valores de fábrica daquele perfil. Botões e eixos não mudam.</summary>
    public void RestoreMotionDefaults()
    {
        Profile.Motion = AppSettings.DefaultMotionFor(Profile.Name);
        Controller.StopAll("padrões de movimento restaurados");
        Save();
    }

    public void Dispose()
    {
        _timer.Stop();
        Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _guardian?.Dispose();
        try { Controller.StopAll("encerrando o programa"); } catch { }
        _dirty = true;   // sempre grava ao sair
        Flush();
        Client.Dispose();
        Pad.Dispose();
    }
}
