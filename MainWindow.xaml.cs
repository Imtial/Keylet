using System.Diagnostics;
using System.ComponentModel;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Keylet;

public partial class MainWindow : Window
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int VK_BACK = 0x08;
    private const int VK_TAB = 0x09;
    private const int VK_RETURN = 0x0D;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_SPACE = 0x20;
    private const int VK_PRIOR = 0x21;
    private const int VK_NEXT = 0x22;
    private const int VK_END = 0x23;
    private const int VK_HOME = 0x24;
    private const int VK_LEFT = 0x25;
    private const int VK_UP = 0x26;
    private const int VK_RIGHT = 0x27;
    private const int VK_DOWN = 0x28;
    private const int VK_DELETE = 0x2E;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_F4 = 0x73;
    private const int VK_MENU = 0x12;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_SHIFT = 0x10;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_CAPITAL = 0x14;
    private const int VK_T = 0x54;
    private const int VK_9 = 0x39;
    private const uint SPI_GETSTICKYKEYS = 0x003A;
    private const uint SPI_SETSTICKYKEYS = 0x003B;
    private const uint SPI_GETTOGGLEKEYS = 0x0034;
    private const uint SPI_SETTOGGLEKEYS = 0x0035;
    private const uint SPI_GETFILTERKEYS = 0x0032;
    private const uint SPI_SETFILTERKEYS = 0x0033;
    private const uint SKF_STICKYKEYSON = 0x00000001;
    private const uint SKF_HOTKEYACTIVE = 0x00000004;
    private const uint SKF_CONFIRMHOTKEY = 0x00000008;
    private const uint TKF_TOGGLEKEYSON = 0x00000001;
    private const uint TKF_HOTKEYACTIVE = 0x00000004;
    private const uint TKF_CONFIRMHOTKEY = 0x00000008;
    private const uint FKF_FILTERKEYSON = 0x00000001;
    private const uint FKF_HOTKEYACTIVE = 0x00000004;
    private const uint FKF_CONFIRMHOTKEY = 0x00000008;

    private static readonly Brush[] Palette =
    [
        new SolidColorBrush(Color.FromRgb(255, 209, 102)),
        new SolidColorBrush(Color.FromRgb(6, 214, 160)),
        new SolidColorBrush(Color.FromRgb(17, 138, 178)),
        new SolidColorBrush(Color.FromRgb(239, 71, 111)),
        new SolidColorBrush(Color.FromRgb(255, 159, 28)),
        new SolidColorBrush(Color.FromRgb(181, 131, 255)),
        new SolidColorBrush(Color.FromRgb(144, 224, 239)),
    ];

    private readonly List<Inline> _typedInlines = [];
    private readonly HashSet<int> _pressedKeys = [];
    private readonly LowLevelKeyboardProc _keyboardProc;
    private IntPtr _keyboardHook;
    private int _colorIndex;
    private StickyKeys _startupStickyKeys;
    private ToggleKeys _startupToggleKeys;
    private FilterKeys _startupFilterKeys;
    private bool _hasAccessibilitySnapshot;
    private bool _canManageLockShortcutPolicy;
    private string? _startupNotice;
    private int? _startupDisableLockWorkstation;

    public MainWindow()
    {
        InitializeComponent();
        _keyboardProc = KeyboardHookCallback;

        foreach (SolidColorBrush brush in Palette)
        {
            brush.Freeze();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!EnsureElevatedOrRelaunch())
        {
            Close();
            return;
        }

        CaptureAccessibilitySettings();
        SetAccessibilityHotkeysEnabled(false);
        if (_canManageLockShortcutPolicy)
        {
            CaptureLockShortcutSettings();
            SetLockShortcutSettingsEnabled(false);
        }

        Show();
        WindowState = WindowState.Maximized;
        Topmost = true;
        Activate();
        Focus();
        _keyboardHook = SetKeyboardHook(_keyboardProc);
        if (_keyboardHook == IntPtr.Zero)
        {
            Debug.WriteLine("KEYLET: keyboard hook install failed.");
            MessageBox.Show("Keyboard hook could not be installed. Key blocking will not work.", "Keylet", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            Topmost = true;
            Activate();
            Focus();
        }, DispatcherPriority.ApplicationIdle);

        if (!string.IsNullOrWhiteSpace(_startupNotice))
        {
            _ = ShowTemporaryStatusAsync(_startupNotice);
        }
    }

    private bool EnsureElevatedOrRelaunch()
    {
        if (IsRunningAsAdministrator())
        {
            _canManageLockShortcutPolicy = true;
            return true;
        }

        string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            MessageBox.Show("Could not determine executable path for elevation.", "Keylet", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }

        string[] args = Environment.GetCommandLineArgs();
        string forwardedArgs = string.Join(" ", args.Skip(1).Select(QuoteArgument));

        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = exePath,
                Arguments = forwardedArgs,
                UseShellExecute = true,
                Verb = "runas",
            };

            Process.Start(startInfo);
            return false;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _canManageLockShortcutPolicy = false;
            _startupNotice = "Win+L still works. Tap Allow next time to block it.";
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"KEYLET: elevation request failed: {ex.Message}");
            _canManageLockShortcutPolicy = false;
            _startupNotice = "Win+L still works. Tap Allow next time to block it.";
            return true;
        }
    }

    private async Task ShowTemporaryStatusAsync(string message)
    {
        StatusToast.Text = message;
        StatusToast.Visibility = Visibility.Visible;
        await Task.Delay(TimeSpan.FromSeconds(3));
        StatusToast.Visibility = Visibility.Collapsed;
    }

    private static bool IsRunningAsAdministrator()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        WindowsPrincipal principal = new(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static string QuoteArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        bool needsQuotes = argument.Any(char.IsWhiteSpace) || argument.Contains('"');
        if (!needsQuotes)
        {
            return argument;
        }

        return $"\"{argument.Replace("\"", "\\\"")}\"";
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SetAccessibilityHotkeysEnabled(true);
        if (_canManageLockShortcutPolicy)
        {
            SetLockShortcutSettingsEnabled(true);
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        int message = wParam.ToInt32();
        bool isKeyDown = message is WM_KEYDOWN or WM_SYSKEYDOWN;
        bool isKeyUp = message is WM_KEYUP or WM_SYSKEYUP;

        if (!isKeyDown && !isKeyUp)
        {
            return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
        }

        KbdLlHookStruct info = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
        int vkCode = (int)info.vkCode;
        if (isKeyDown)
        {
            _pressedKeys.Add(vkCode);
        }
        else
        {
            _pressedKeys.Remove(vkCode);
        }

        if (isKeyDown && IsParentExit(vkCode))
        {
            Dispatcher.BeginInvoke(Close, DispatcherPriority.Send);
            return 1;
        }

        if (isKeyDown)
        {
            HandleKeyDown(vkCode, info.scanCode);
        }

        // The app is intentionally a foreground keyboard sandbox. Swallow all keyboard
        // messages so app/window switching shortcuts do not reach Explorer or other apps.
        return 1;
    }

    private void HandleKeyDown(int vkCode, uint scanCode)
    {
        if (IsBlockedSystemKey(vkCode))
        {
            return;
        }

        if (vkCode == VK_BACK)
        {
            Dispatcher.BeginInvoke(RemoveLastInline);
            return;
        }

        if (vkCode == VK_RETURN)
        {
            Dispatcher.BeginInvoke(() => AddInline(new LineBreak()));
            return;
        }

        string? text = TryGetText(vkCode, scanCode);
        if (!string.IsNullOrEmpty(text))
        {
            Dispatcher.BeginInvoke(() => AppendText(text));
        }
    }

    private bool IsParentExit(int vkCode)
    {
        return (vkCode == VK_T || vkCode == VK_9) && IsControlDown() && IsAltDown() && IsPressed(VK_T) && IsPressed(VK_9);
    }

    private bool IsBlockedSystemKey(int vkCode)
    {
        bool altDown = IsAltDown();
        bool ctrlDown = IsControlDown();
        bool winDown = IsPressed(VK_LWIN) || IsPressed(VK_RWIN);

        return vkCode is VK_LWIN or VK_RWIN
            || winDown
            || (altDown && vkCode is VK_TAB or VK_ESCAPE or VK_F4)
            || (ctrlDown && vkCode == VK_ESCAPE)
            || vkCode is VK_PRIOR or VK_NEXT or VK_END or VK_HOME or VK_LEFT or VK_UP or VK_RIGHT or VK_DOWN or VK_DELETE;
    }

    private void AppendText(string text)
    {
        foreach (char character in text)
        {
            if (char.IsControl(character))
            {
                continue;
            }

            Run run = new(character.ToString())
            {
                Foreground = Palette[_colorIndex++ % Palette.Length],
                FontSize = character == ' ' ? 102 : 94 + ((_colorIndex % 4) * 7),
            };

            AddInline(run);
        }
    }

    private void AddInline(Inline inline)
    {
        TypedText.Inlines.Add(inline);
        _typedInlines.Add(inline);
        EmptyHint.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(() => ResetViewIfOverflow(inline), DispatcherPriority.Loaded);
    }

    private void RemoveLastInline()
    {
        if (_typedInlines.Count == 0)
        {
            return;
        }

        Inline inline = _typedInlines[^1];
        _typedInlines.RemoveAt(_typedInlines.Count - 1);
        TypedText.Inlines.Remove(inline);

        if (_typedInlines.Count == 0)
        {
            EmptyHint.Visibility = Visibility.Visible;
        }
    }

    private void ResetViewIfOverflow(Inline newestInline)
    {
        if (_typedInlines.Count == 0 || TypedText.ActualWidth <= 0 || TypedText.ActualHeight <= 0)
        {
            return;
        }

        Size available = new(TypedText.ActualWidth, double.PositiveInfinity);
        TypedText.Measure(available);

        if (TypedText.DesiredSize.Height <= TypedText.ActualHeight)
        {
            return;
        }

        bool keepNewestInline = newestInline.Parent == TypedText && newestInline is not LineBreak;
        TypedText.Inlines.Clear();
        _typedInlines.Clear();

        if (keepNewestInline)
        {
            TypedText.Inlines.Add(newestInline);
            _typedInlines.Add(newestInline);
            EmptyHint.Visibility = Visibility.Collapsed;
            return;
        }

        EmptyHint.Visibility = Visibility.Visible;
    }

    private string? TryGetText(int vkCode, uint scanCode)
    {
        byte[] keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
        {
            return null;
        }

        ApplyPressedState(keyboardState, VK_SHIFT, IsShiftDown());
        ApplyPressedState(keyboardState, VK_CONTROL, IsControlDown());
        ApplyPressedState(keyboardState, VK_MENU, IsAltDown());

        if ((GetKeyState(VK_CAPITAL) & 0x0001) != 0)
        {
            keyboardState[VK_CAPITAL] |= 0x01;
        }

        StringBuilder buffer = new(8);
        IntPtr layout = GetKeyboardLayout(0);
        int result = ToUnicodeEx((uint)vkCode, scanCode, keyboardState, buffer, buffer.Capacity, 0, layout);

        return result > 0 ? buffer.ToString(0, result) : vkCode == VK_SPACE ? " " : null;
    }

    private static void ApplyPressedState(byte[] keyboardState, int virtualKey, bool isPressed)
    {
        if (isPressed)
        {
            keyboardState[virtualKey] |= 0x80;
        }
    }

    private bool IsShiftDown()
    {
        return IsPressed(VK_SHIFT) || IsPressed(VK_LSHIFT) || IsPressed(VK_RSHIFT);
    }

    private bool IsControlDown()
    {
        return IsPressed(VK_CONTROL) || IsPressed(VK_LCONTROL) || IsPressed(VK_RCONTROL);
    }

    private bool IsAltDown()
    {
        return IsPressed(VK_MENU) || IsPressed(VK_LMENU) || IsPressed(VK_RMENU);
    }

    private bool IsPressed(int virtualKey)
    {
        return _pressedKeys.Contains(virtualKey) || IsDown(virtualKey);
    }

    private static bool IsDown(int virtualKey)
    {
        return (GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using Process currentProcess = Process.GetCurrentProcess();
        using ProcessModule currentModule = currentProcess.MainModule!;
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(currentModule.ModuleName), 0);
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private void CaptureLockShortcutSettings()
    {
        _startupDisableLockWorkstation = ReadDwordValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableLockWorkstation");
    }

    private void SetLockShortcutSettingsEnabled(bool enabled)
    {
        if (enabled)
        {
            RestoreDwordValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableLockWorkstation", _startupDisableLockWorkstation);
            return;
        }

        // Disable lock shortcut (Win+L) while Keylet is active.
        WriteDwordValue(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Policies\System", "DisableLockWorkstation", 1);
    }

    private static int? ReadDwordValue(RegistryKey root, string subKey, string valueName)
    {
        using RegistryKey? key = root.OpenSubKey(subKey, writable: false);
        object? value = key?.GetValue(valueName);

        if (value is int intValue)
        {
            return intValue;
        }

        return null;
    }

    private static void WriteDwordValue(RegistryKey root, string subKey, string valueName, int value)
    {
        try
        {
            using RegistryKey key = root.CreateSubKey(subKey, writable: true)!;
            key.SetValue(valueName, value, RegistryValueKind.DWord);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip keys protected by policy ACLs.
        }
        catch (SecurityException)
        {
            // Skip keys blocked by security policy.
        }
    }

    private static void RestoreDwordValue(RegistryKey root, string subKey, string valueName, int? priorValue)
    {
        try
        {
            using RegistryKey key = root.CreateSubKey(subKey, writable: true)!;

            if (priorValue.HasValue)
            {
                key.SetValue(valueName, priorValue.Value, RegistryValueKind.DWord);
                return;
            }

            key.DeleteValue(valueName, throwOnMissingValue: false);
        }
        catch (UnauthorizedAccessException)
        {
            // Skip keys protected by policy ACLs.
        }
        catch (SecurityException)
        {
            // Skip keys blocked by security policy.
        }
    }

    private void CaptureAccessibilitySettings()
    {
        _startupStickyKeys = StickyKeys.Create();
        _startupToggleKeys = ToggleKeys.Create();
        _startupFilterKeys = FilterKeys.Create();

        bool stickyOk = SystemParametersInfo(SPI_GETSTICKYKEYS, (uint)Marshal.SizeOf<StickyKeys>(), ref _startupStickyKeys, 0);
        bool toggleOk = SystemParametersInfo(SPI_GETTOGGLEKEYS, (uint)Marshal.SizeOf<ToggleKeys>(), ref _startupToggleKeys, 0);
        bool filterOk = SystemParametersInfo(SPI_GETFILTERKEYS, (uint)Marshal.SizeOf<FilterKeys>(), ref _startupFilterKeys, 0);
        _hasAccessibilitySnapshot = stickyOk && toggleOk && filterOk;
    }

    private void SetAccessibilityHotkeysEnabled(bool enabled)
    {
        if (!_hasAccessibilitySnapshot)
        {
            return;
        }

        if (enabled)
        {
            StickyKeys stickyRestore = _startupStickyKeys;
            ToggleKeys toggleRestore = _startupToggleKeys;
            FilterKeys filterRestore = _startupFilterKeys;

            SystemParametersInfo(SPI_SETSTICKYKEYS, (uint)Marshal.SizeOf<StickyKeys>(), ref stickyRestore, 0);
            SystemParametersInfo(SPI_SETTOGGLEKEYS, (uint)Marshal.SizeOf<ToggleKeys>(), ref toggleRestore, 0);
            SystemParametersInfo(SPI_SETFILTERKEYS, (uint)Marshal.SizeOf<FilterKeys>(), ref filterRestore, 0);
            return;
        }

        StickyKeys stickyOff = _startupStickyKeys;
        if ((stickyOff.dwFlags & SKF_STICKYKEYSON) == 0)
        {
            stickyOff.dwFlags &= ~SKF_HOTKEYACTIVE;
            stickyOff.dwFlags &= ~SKF_CONFIRMHOTKEY;
            SystemParametersInfo(SPI_SETSTICKYKEYS, (uint)Marshal.SizeOf<StickyKeys>(), ref stickyOff, 0);
        }

        ToggleKeys toggleOff = _startupToggleKeys;
        if ((toggleOff.dwFlags & TKF_TOGGLEKEYSON) == 0)
        {
            toggleOff.dwFlags &= ~TKF_HOTKEYACTIVE;
            toggleOff.dwFlags &= ~TKF_CONFIRMHOTKEY;
            SystemParametersInfo(SPI_SETTOGGLEKEYS, (uint)Marshal.SizeOf<ToggleKeys>(), ref toggleOff, 0);
        }

        FilterKeys filterOff = _startupFilterKeys;
        if ((filterOff.dwFlags & FKF_FILTERKEYSON) == 0)
        {
            filterOff.dwFlags &= ~FKF_HOTKEYACTIVE;
            filterOff.dwFlags &= ~FKF_CONFIRMHOTKEY;
            SystemParametersInfo(SPI_SETFILTERKEYS, (uint)Marshal.SizeOf<FilterKeys>(), ref filterOff, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StickyKeys
    {
        public uint cbSize;
        public uint dwFlags;

        public static StickyKeys Create()
        {
            return new StickyKeys
            {
                cbSize = (uint)Marshal.SizeOf<StickyKeys>(),
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ToggleKeys
    {
        public uint cbSize;
        public uint dwFlags;

        public static ToggleKeys Create()
        {
            return new ToggleKeys
            {
                cbSize = (uint)Marshal.SizeOf<ToggleKeys>(),
            };
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FilterKeys
    {
        public uint cbSize;
        public uint dwFlags;
        public uint iWaitMSec;
        public uint iDelayMSec;
        public uint iRepeatMSec;
        public uint iBounceMSec;

        public static FilterKeys Create()
        {
            return new FilterKeys
            {
                cbSize = (uint)Marshal.SizeOf<FilterKeys>(),
            };
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags,
        IntPtr dwhkl);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref StickyKeys pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref ToggleKeys pvParam, uint fWinIni);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref FilterKeys pvParam, uint fWinIni);
}
