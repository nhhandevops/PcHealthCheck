using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PcHealth.Modules.Windows.Support;

/// <summary>
/// Offline Authenticode verification via WinVerifyTrust.
/// PRIVACY: WTD_CACHE_ONLY_URL_RETRIEVAL + WTD_REVOKE_NONE + WTD_REVOCATION_CHECK_NONE
/// keep the check fully offline — the Windows default would silently fetch CRL/OCSP data
/// from the internet for every startup binary we inspect. Online revocation checking, if
/// ever offered, is a separately consented feature.
///
/// A signature lives in one of two places. Most programs carry it inside the file. Many of
/// Windows' own files carry none: Microsoft signs a catalog (a .cat file in System32\CatRoot)
/// that lists their hashes instead. Checking only the embedded signature read Windows
/// Security's own tray icon, SecurityHealthSystray.exe, as unsigned on every Windows PC
/// (SIG-001). So a file with no embedded signature is looked up in Windows' catalog database,
/// on this PC, and the catalog that lists it is verified with the same offline flags.
///
/// Expired certificates: WinVerifyTrust already judges a timestamped signature by the time it
/// was made. LightShot's certificate expired in 2022 and its file verifies (status 0, checked on
/// the dev T470p on 2026-10-27). CERT_E_EXPIRED therefore means that no timestamp shows the file
/// was signed while the certificate was valid. The file's digest is checked before its
/// certificate, so that is not tampering, and it gets its own state rather than Invalid.
/// </summary>
public static partial class AuthenticodeVerifier
{
    public enum SignatureState
    {
        SignedAndValid,
        NotSigned,
        Invalid,
        Unknown,

        /// <summary>Signed, but the certificate has expired and no timestamp shows the
        /// signature was made in time. Windows no longer accepts it; nothing says it was altered.</summary>
        Expired,
    }

    /// <summary>Who signed a file, as the signing certificate's subject names them.</summary>
    public sealed record Signer(string? CommonName, string? Organization)
    {
        /// <summary>The name Windows shows as "Name of signer" (the CN), or the organisation
        /// when a certificate has no CN.</summary>
        public string Display => !string.IsNullOrWhiteSpace(CommonName) ? CommonName : Organization ?? "";
    }

    /// <summary>
    /// What was found for one file. <see cref="Status"/> is WinVerifyTrust's own answer, kept so
    /// a file that could not be verified can say why. <see cref="Failure"/> is set only when the
    /// check itself could not run (the verifier or the catalog database failed), which is not
    /// the same as Windows answering that a signature does not lead to a trusted root.
    /// </summary>
    public sealed record Result(
        SignatureState State,
        Signer? Signer = null,
        bool ViaCatalog = false,
        int Status = 0,
        string? Failure = null);

    public static Result Verify(string filePath)
    {
        try
        {
            var embedded = VerifyEmbedded(filePath);
            return embedded.State == SignatureState.NotSigned
                ? VerifyByCatalog(filePath) ?? embedded
                : embedded;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return new Result(SignatureState.Unknown, Failure: $"the signature verifier could not be loaded ({e.GetType().Name})");
        }
    }

    /// <summary>
    /// What a WinVerifyTrust status means here. Public so the mapping can be tested without a
    /// file that produces each status.
    /// </summary>
    public static SignatureState Classify(int status) => status switch
    {
        0 => SignatureState.SignedAndValid,
        TRUST_E_NOSIGNATURE or TRUST_E_SUBJECT_FORM_UNKNOWN or TRUST_E_PROVIDER_UNKNOWN => SignatureState.NotSigned,
        TRUST_E_EXPLICIT_DISTRUST or TRUST_E_SUBJECT_NOT_TRUSTED or TRUST_E_BAD_DIGEST => SignatureState.Invalid,
        CERT_E_EXPIRED => SignatureState.Expired,
        _ => SignatureState.Unknown,
    };

    private static unsafe Result VerifyEmbedded(string filePath)
    {
        var path = Marshal.StringToHGlobalUni(filePath);
        try
        {
            var fileInfo = new WINTRUST_FILE_INFO
            {
                cbStruct = (uint)sizeof(WINTRUST_FILE_INFO),
                pcwszFilePath = path,
            };
            return Run(WTD_CHOICE_FILE, (IntPtr)(&fileInfo), viaCatalog: false);
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }
    }

    /// <summary>
    /// Looks the file up in Windows' catalog database and verifies the catalog that lists it.
    /// Null when no catalog lists it, or when the file is one no catalog can list, so the embedded
    /// check's "not signed" stands. Catalogs are indexed by SHA-256 and, for older ones, SHA-1, so
    /// both are tried.
    /// </summary>
    private static Result? VerifyByCatalog(string filePath)
    {
        foreach (var algorithm in new[] { "SHA256", "SHA1" })
        {
            if (!CryptCATAdminAcquireContext2(out var admin, IntPtr.Zero, algorithm, IntPtr.Zero, 0))
                return CatalogFailure("the catalog database could not be opened", HResultFromWin32(Marshal.GetLastPInvokeError()));
            try
            {
                // A file that cannot be hashed is a fact about that file, not a check that broke.
                // Recording it as a failure put "some checks could not finish" on a healthy PC whose
                // startup list named a 0-byte file (SIG-001, review of 2026-10-28). An empty file or
                // a truncated program is in no catalog, so "not signed" stands; any other reason
                // leaves the file unverified, and the Note gives Windows' answer.
                var hash = HashForCatalog(admin, filePath, out var hashError, out var notHashable);
                if (hash is null) return notHashable ? null : new Result(SignatureState.Unknown, Status: hashError);

                var catalog = FindCatalog(admin, hash, out var lookupError);
                if (lookupError != 0) return CatalogFailure("the catalog lookup failed", lookupError);
                if (catalog is null) continue;

                return VerifyCatalogMember(admin, catalog, filePath, hash);
            }
            finally
            {
                _ = CryptCATAdminReleaseContext(admin, 0);
            }
        }
        return null;
    }

    /// <param name="error">An HRESULT, so every failure reads the same way: 0x8007xxxx for a Windows error.</param>
    private static Result CatalogFailure(string what, int error) =>
        new(SignatureState.Unknown, Status: error,
            Failure: $"{what} (error 0x{error.ToString("X8", CultureInfo.InvariantCulture)})");

    /// <param name="notHashable">True when the hasher refused the file for what it holds: Windows
    /// answers ERROR_FILE_INVALID (1006) for an empty file, and 0x800700C1, ERROR_BAD_EXE_FORMAT
    /// already as an HRESULT, for a program cut short at 4 KB (both seen on the dev T470p,
    /// 2026-10-28, under SHA-256 and SHA-1 alike).</param>
    private static unsafe byte[]? HashForCatalog(IntPtr admin, string filePath, out int error, out bool notHashable)
    {
        error = 0;
        notHashable = false;
        try
        {
            using var file = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[64];   // room for any hash the catalog database uses
            var size = (uint)buffer.Length;
            fixed (byte* pointer = buffer)
            {
                if (!CryptCATAdminCalcHashFromFileHandle2(admin, file.DangerousGetHandle(), &size, pointer, 0))
                {
                    error = HResultFromWin32(Marshal.GetLastPInvokeError());
                    notHashable = error == HResultFromWin32(ERROR_FILE_INVALID) || error == HResultFromWin32(ERROR_BAD_EXE_FORMAT);
                    return null;
                }
            }
            return buffer[..(int)size];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.HResult;
            return null;
        }
    }

    /// <summary>The path of the first catalog that lists this hash, or null when none does.
    /// "Not found" is the ordinary answer for any non-Windows file and is not an error.</summary>
    private static unsafe string? FindCatalog(IntPtr admin, byte[] hash, out int error)
    {
        error = 0;
        IntPtr info;
        fixed (byte* pointer = hash)
        {
            info = CryptCATAdminEnumCatalogFromHash(admin, pointer, (uint)hash.Length, 0, IntPtr.Zero);
        }
        if (info == IntPtr.Zero)
        {
            var lastError = Marshal.GetLastPInvokeError();
            if (lastError != ERROR_NOT_FOUND) error = HResultFromWin32(lastError);
            return null;
        }

        try
        {
            var catalog = new CATALOG_INFO { cbStruct = (uint)sizeof(CATALOG_INFO) };
            if (!CryptCATCatalogInfoFromContext(info, &catalog, 0))
            {
                error = HResultFromWin32(Marshal.GetLastPInvokeError());
                return null;
            }
            return new string(catalog.wszCatalogFile);
        }
        finally
        {
            _ = CryptCATAdminReleaseCatalogContext(admin, info, 0);
        }
    }

    private static unsafe Result VerifyCatalogMember(IntPtr admin, string catalogPath, string filePath, byte[] hash)
    {
        var catalog = Marshal.StringToHGlobalUni(catalogPath);
        var member = Marshal.StringToHGlobalUni(filePath);
        var tag = Marshal.StringToHGlobalUni(Convert.ToHexString(hash));   // members are tagged by upper-case hex hash
        try
        {
            fixed (byte* hashPointer = hash)
            {
                var info = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)sizeof(WINTRUST_CATALOG_INFO),
                    pcwszCatalogFilePath = catalog,
                    pcwszMemberTag = tag,
                    pcwszMemberFilePath = member,
                    pbCalculatedFileHash = (IntPtr)hashPointer,
                    cbCalculatedFileHash = (uint)hash.Length,
                    hCatAdmin = admin,
                };
                return Run(WTD_CHOICE_CATALOG, (IntPtr)(&info), viaCatalog: true);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(tag);
            Marshal.FreeHGlobal(member);
            Marshal.FreeHGlobal(catalog);
        }
    }

    private static Result Run(uint unionChoice, IntPtr unionInfo, bool viaCatalog)
    {
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = unionChoice,
            pUnion = unionInfo,
            dwStateAction = WTD_STATEACTION_VERIFY,
            dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE,
        };

        var actionId = WINTRUST_ACTION_GENERIC_VERIFY_V2;
        var status = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
        try
        {
            var state = Classify(status);
            // Read while the verification state is still open: it is the certificate
            // WinVerifyTrust itself used, so the name and the verdict belong together.
            var signer = state == SignatureState.NotSigned ? null : ReadSigner(data.hWVTStateData);
            return new Result(state, signer, viaCatalog, status);
        }
        finally
        {
            // Release verification state regardless of outcome.
            data.dwStateAction = WTD_STATEACTION_CLOSE;
            _ = WinVerifyTrust(IntPtr.Zero, ref actionId, ref data);
        }
    }

    /// <summary>
    /// The first signer's certificate subject: CN and O. Null when Windows built no signer, which
    /// happens when the signature could not be parsed at all. A name that cannot be read leaves
    /// the verdict as it is; it only means the report shows no name.
    /// </summary>
    private static Signer? ReadSigner(IntPtr stateData)
    {
        if (stateData == IntPtr.Zero) return null;
        var provider = WTHelperProvDataFromStateData(stateData);
        if (provider == IntPtr.Zero) return null;
        var signer = WTHelperGetProvSignerFromChain(provider, 0, 0, 0);
        if (signer == IntPtr.Zero) return null;
        var providerCert = WTHelperGetProvCertFromChain(signer, 0);
        if (providerCert == IntPtr.Zero) return null;

        // CRYPT_PROVIDER_CERT begins { DWORD cbStruct; PCCERT_CONTEXT pCert; }, and the pointer is
        // aligned to its own size on both x86 and x64.
        var certContext = Marshal.ReadIntPtr(providerCert, IntPtr.Size);
        if (certContext == IntPtr.Zero) return null;

        try
        {
            using var certificate = new X509Certificate2(certContext);
            var subject = certificate.SubjectName;
            var signerName = new Signer(Attribute(subject, OidCommonName), Attribute(subject, OidOrganization));
            return signerName.Display.Length == 0 ? null : signerName;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static string? Attribute(X500DistinguishedName name, string oid) =>
        name.EnumerateRelativeDistinguishedNames()
            .Where(rdn => !rdn.HasMultipleElements && rdn.GetSingleElementType().Value == oid)
            .Select(rdn => rdn.GetSingleElementValue()?.Trim())
            .FirstOrDefault(value => !string.IsNullOrEmpty(value));

    private static int HResultFromWin32(int error) =>
        error <= 0 ? error : unchecked((int)(((uint)error & 0x0000FFFF) | 0x80070000));

    private const string OidCommonName = "2.5.4.3";
    private const string OidOrganization = "2.5.4.10";

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_CHOICE_CATALOG = 2;
    private const uint WTD_STATEACTION_VERIFY = 1;
    private const uint WTD_STATEACTION_CLOSE = 2;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;

    private const int ERROR_NOT_FOUND = 1168;
    private const int ERROR_BAD_EXE_FORMAT = 193;
    private const int ERROR_FILE_INVALID = 1006;

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

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperProvDataFromStateData(IntPtr hStateData);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvSignerFromChain(IntPtr pProvData, uint idxSigner, int fCounterSigner, uint idxCounterSigner);

    [LibraryImport("wintrust.dll")]
    private static partial IntPtr WTHelperGetProvCertFromChain(IntPtr pSgnr, uint idxCert);

    [LibraryImport("wintrust.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, uint* pcbHash, byte* pbHash, uint dwFlags);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    private static unsafe partial IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte* pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [LibraryImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, CATALOG_INFO* psCatInfo, uint dwFlags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [LibraryImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public IntPtr pcwszCatalogFilePath;
        public IntPtr pcwszMemberTag;
        public IntPtr pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CATALOG_INFO
    {
        public uint cbStruct;
        public fixed char wszCatalogFile[260];   // MAX_PATH
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
        public IntPtr pUnion;   // WINTRUST_FILE_INFO* or WINTRUST_CATALOG_INFO*, per dwUnionChoice
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
