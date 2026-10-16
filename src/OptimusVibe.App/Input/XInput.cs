using System.Runtime.InteropServices;

namespace OptimusVibe.App.Input;

[Flags]
public enum GamepadButtons : ushort
{
    None = 0,
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Start = 0x0010,
    Back = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputGamepad
{
    public GamepadButtons Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
public struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

/// <summary>XInputGetState via xinput1_4.dll (Windows 8+), falling back to xinput9_1_0.dll.</summary>
public static class XInput
{
    public const int MaxControllers = 4;
    private const int ErrorSuccess = 0;

    private static int _dll; // 0 = untried, 1 = xinput1_4, 2 = xinput9_1_0, -1 = unavailable

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState14(int userIndex, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern int GetState910(int userIndex, out XInputState state);

    public static bool IsAvailable => _dll >= 0;

    /// <summary>True when controller <paramref name="index"/> is connected.</summary>
    public static bool TryGetState(int index, out XInputState state)
    {
        state = default;
        if (_dll == 0 || _dll == 1)
        {
            try
            {
                var r = GetState14(index, out state);
                _dll = 1;
                return r == ErrorSuccess;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _dll = 2;
            }
        }
        if (_dll == 2)
        {
            try
            {
                return GetState910(index, out state) == ErrorSuccess;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _dll = -1;
            }
        }
        return false;
    }
}
