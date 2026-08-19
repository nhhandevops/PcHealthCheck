using System.Runtime.InteropServices;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Offline Authenticode verification via WinVerifyTrust.
/// PRIVACY: WTD_CACHE_ONLY_URL_RETRIEVAL + WTD_REVOKE_NONE + WTD_REVOCATION_CHECK_NONE
/// keep the check fully offline — the Windows default would silently fetch CRL/OCSP data
/// from the internet for every startup binary we inspect. Online revocation checking, if
/// ever offered, is a separately consented feature.
/// </summary>
internal static partial class AuthenticodeVerifier
{
    public enum SignatureState
    {
        SignedAndValid,
        NotSigned,
        Invalid,
        Unknown,
    }

    public static SignatureState Verify(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
        };

        var fileInfoPtr = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            data.pFile = fileInfoPtr;

            var actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            var status = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

            // Release verification state regardless of outcome.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);

            return status switch
            {
                0 => SignatureState.SignedAndValid,
                TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureState.NotSigned,
                TRUST_E_EXPLICIT_DISTRUST or TRUST_E_SUBJECT_NOT_TRUSTED or TRUST_E_BAD_DIGEST or CERT_E_EXPIRED => SignatureState.Invalid,
                _ => SignatureState.Unknown,
            };
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return SignatureState.Unknown;
        }
        finally
        {
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private const int TRUST_E_NOSIGNATURE = unchecked((int)0x800B0100);
    private const int TRUST_E_SUBJECT_FORM_UNKNOWN = unchecked((int)0x800B0003);
    private const int TRUST_E_PROVIDER_UNKNOWN = unchecked((int)0x800B0001);
    private const int TRUST_E_EXPLICIT_DISTRUST = unchecked((int)0x800B0111);
    private const int TRUST_E_SUBJECT_NOT_TRUSTED = unchecked((int)0x800B0004);
    private const int TRUST_E_BAD_DIGEST = unchecked((int)0x80096010);
    private const int CERT_E_EXPIRED = unchecked((int)0x800B0101);

    private static Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [LibraryImport("wintrust.dll", SetLastError = false)]
    private static partial int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
