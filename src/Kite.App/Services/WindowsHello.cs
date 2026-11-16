using System.Runtime.InteropServices;

namespace Kite.App.Services;

/// <summary>
/// Windows Hello (face, fingerprint or PIN) via UserConsentVerifier, called through its COM interop
/// interface so Kite doesn't need the large Windows SDK projection. Fails closed: anything unexpected
/// is reported as not verified.
/// </summary>
public static class WindowsHello
{
    public enum Result { Verified = 0, DeviceNotPresent = 1, NotConfiguredForUser = 2, DisabledByPolicy = 3, DeviceBusy = 4, RetriesExhausted = 5, Canceled = 6, Failed = 100 }

    private static readonly Guid IidUserConsentVerifierInterop = new("39E050C3-4E74-441A-8DC0-B81104DF949C");
    // IAsyncOperation<UserConsentVerificationResult>: the WinRT parameterized-interface IID, derived from
    // pinterface({9fc2b0bb-e446-4e1a-9ce6-a7ab2aab0ab5};enum(Windows.Security.Credentials.UI.UserConsentVerificationResult;i4)).
    private static readonly Guid IidAsyncOperationOfResult = new("e5189ae1-5e10-58ce-81da-4ae5660a1f39");
    private static readonly Guid IidAsyncInfo = new("00000036-0000-0000-C000-000000000046");

    /// <summary>Shows the Windows Hello prompt for <paramref name="owner"/>. Call on a thread with COM initialised (the UI thread).</summary>
    public static async Task<Result> VerifyAsync(IntPtr owner, string message)
    {
        IntPtr factory = IntPtr.Zero, operation = IntPtr.Zero, info = IntPtr.Zero, className = IntPtr.Zero, text = IntPtr.Zero;
        try
        {
            const string runtimeClass = "Windows.Security.Credentials.UI.UserConsentVerifier";
            if (WindowsCreateString(runtimeClass, runtimeClass.Length, out className) < 0) return Result.Failed;
            var iid = IidUserConsentVerifierInterop;
            if (RoGetActivationFactory(className, ref iid, out factory) < 0) return Result.Failed;
            if (WindowsCreateString(message, message.Length, out text) < 0) return Result.Failed;

            var request = Method<RequestVerificationForWindowAsync>(factory, 6);
            var opIid = IidAsyncOperationOfResult;
            var hr = request(factory, owner, text, ref opIid, out operation);
            if (hr < 0 || operation == IntPtr.Zero)
            {
                Logger.Log($"Windows Hello request failed: 0x{hr:X8}");
                return Result.Failed;
            }
            if (Marshal.QueryInterface(operation, in IidAsyncInfo, out info) < 0) return Result.Failed;

            var getStatus = Method<GetInt>(info, 7); // IAsyncInfo.get_Status
            int status;
            while (true)
            {
                if (getStatus(info, out status) < 0) return Result.Failed;
                if (status != 0) break; // 0 = Started
                await Task.Delay(100);
            }
            if (status == 2) return Result.Canceled;      // Canceled
            if (status != 1) return Result.Failed;        // 3 = Error
            var getResults = Method<GetInt>(operation, 8); // IAsyncOperation<T>.GetResults
            return getResults(operation, out var result) < 0 ? Result.Failed : (Result)result;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or COMException or InvalidCastException)
        {
            Logger.Log($"Windows Hello unavailable: {ex.Message}");
            return Result.Failed;
        }
        finally
        {
            if (info != IntPtr.Zero) Marshal.Release(info);
            if (operation != IntPtr.Zero) Marshal.Release(operation);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            if (text != IntPtr.Zero) WindowsDeleteString(text);
            if (className != IntPtr.Zero) WindowsDeleteString(className);
        }
    }

    /// <summary>What to tell the user when verification didn't succeed (null when it did, or when they cancelled).</summary>
    public static string? Explain(Result result) => result switch
    {
        Result.Verified or Result.Canceled => null,
        Result.DeviceNotPresent or Result.NotConfiguredForUser => "Windows Hello isn't set up on this PC. Set it up in Windows Settings › Accounts › Sign-in options, or turn off the check in Kite's settings.",
        Result.DisabledByPolicy => "Windows Hello is turned off by your organisation's policy. Turn off the check in Kite's settings to use codes here.",
        Result.DeviceBusy => "Windows Hello is busy. Try again in a moment.",
        Result.RetriesExhausted => "Too many attempts. Try again later.",
        _ => "Windows Hello couldn't be started.",
    };

    private static T Method<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RequestVerificationForWindowAsync(IntPtr self, IntPtr hwnd, IntPtr message, ref Guid riid, out IntPtr operation);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInt(IntPtr self, out int value);

    [DllImport("combase.dll", CharSet = CharSet.Unicode)] private static extern int WindowsCreateString(string source, int length, out IntPtr hstring);
    [DllImport("combase.dll")] private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll")] private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);
}
