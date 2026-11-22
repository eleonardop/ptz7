using System.Runtime.InteropServices;
using PtzStudio.Core.Input;

namespace PtzStudio.App.Input;

/// <summary>Portas XInput do Windows (xinput1_4.dll, presente no Windows 10 e 11). Só consulta presença e envia vibração ZERO.</summary>
internal sealed class XInputPorts : IXInputPorts
{
    private const uint Ok = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad { public ushort Buttons; public byte LeftTrigger, RightTrigger; public short ThumbLX, ThumbLY, ThumbRX, ThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState { public uint PacketNumber; public XInputGamepad Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputVibration { public ushort LeftMotorSpeed, RightMotorSpeed; }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")] private static extern uint GetState(uint index, out XInputState state);
    [DllImport("xinput1_4.dll", EntryPoint = "XInputSetState")] private static extern uint SetState(uint index, ref XInputVibration vibration);

    public bool IsConnected(int port)
    {
        try { return GetState((uint)port, out _) == Ok; }      // o estado lido é descartado: os comandos vêm só do DirectInput
        catch { return false; }                               // DLL ausente (não deveria acontecer no Windows 10/11): sem sinal
    }

    public bool SendZeroVibration(int port)
    {
        try
        {
            var zero = new XInputVibration { LeftMotorSpeed = 0, RightMotorSpeed = 0 };
            return SetState((uint)port, ref zero) == Ok;
        }
        catch { return false; }
    }
}
