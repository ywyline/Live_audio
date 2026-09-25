using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace TikTokAudio.Infrastructure.Persistence;

public enum CredentialStatus { Succeeded, NotFound, Unsupported, Cancelled, Failed }

public sealed class CredentialSecret : IDisposable
{
    internal const int MaximumBytes = 2560;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly object _gate = new();
    private byte[]? _bytes;

    public CredentialSecret(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        if (Utf8.GetByteCount(value) > MaximumBytes)
            throw new ArgumentException("The credential exceeds the Windows generic credential limit.", nameof(value));
        _bytes = Utf8.GetBytes(value);
    }

    internal CredentialSecret(byte[] value)
    {
        if (value.Length is < 1 or > MaximumBytes) throw new ArgumentException("The credential size is invalid.", nameof(value));
        _ = Utf8.GetCharCount(value);
        _bytes = value.ToArray();
    }

    public string Reveal()
    {
        lock (_gate)
            return Utf8.GetString(_bytes ?? throw new ObjectDisposedException(nameof(CredentialSecret)));
    }

    internal byte[] CopyBytes()
    {
        lock (_gate)
            return (_bytes ?? throw new ObjectDisposedException(nameof(CredentialSecret))).ToArray();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_bytes is null) return;
            CryptographicOperations.ZeroMemory(_bytes);
            _bytes = null;
        }
    }

    public override string ToString() => "[REDACTED]";
}

public sealed class CredentialReadResult
{
    public CredentialReadResult(CredentialStatus status, CredentialSecret? secret = null)
    {
        if (!Enum.IsDefined(status) || (status == CredentialStatus.Succeeded) != (secret is not null))
            throw new ArgumentException("A successful credential read must contain exactly one secret.", nameof(status));
        Status = status;
        Secret = secret;
    }

    public CredentialStatus Status { get; }
    public CredentialSecret? Secret { get; }
    public override string ToString() => Status.ToString();
}

public interface IWindowsCredentialApi
{
    bool IsSupported { get; }
    CredentialStatus Write(string targetName, CredentialSecret secret);
    CredentialReadResult Read(string targetName);
    CredentialStatus Delete(string targetName);
}

/// <summary>Only explicitly requested credentials in this product's namespace can be accessed.</summary>
public sealed class WindowsCredentialStore
{
    private readonly string _prefix;
    private readonly IWindowsCredentialApi _api;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WindowsCredentialStore(string productName, IWindowsCredentialApi? api = null)
    {
        ValidateName(productName, nameof(productName));
        _prefix = $"LiveAudio:{productName}:";
        _api = api ?? new NativeCredentialApi();
    }

    public Task<CredentialStatus> WriteAsync(string credentialName, CredentialSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(secret);
        var target = Target(credentialName);
        return RunStatusAsync(() => _api.Write(target, secret), cancellationToken);
    }

    public Task<CredentialStatus> DeleteAsync(string credentialName, CancellationToken cancellationToken = default)
    {
        var target = Target(credentialName);
        return RunStatusAsync(() => _api.Delete(target), cancellationToken);
    }

    public async Task<CredentialReadResult> ReadAsync(string credentialName, CancellationToken cancellationToken = default)
    {
        var target = Target(credentialName);
        var entered = false;
        try
        {
            entered = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) return new CredentialReadResult(CredentialStatus.Failed);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_api.IsSupported) return new CredentialReadResult(CredentialStatus.Unsupported);
            return await Task.Run(() => _api.Read(target), cancellationToken).ConfigureAwait(false)
                ?? new CredentialReadResult(CredentialStatus.Failed);
        }
        catch (OperationCanceledException) { return new CredentialReadResult(CredentialStatus.Cancelled); }
        catch (Exception) { return new CredentialReadResult(CredentialStatus.Failed); }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private async Task<CredentialStatus> RunStatusAsync(Func<CredentialStatus> operation, CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            entered = await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) return CredentialStatus.Failed;
            cancellationToken.ThrowIfCancellationRequested();
            if (!_api.IsSupported) return CredentialStatus.Unsupported;
            var status = await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            return Enum.IsDefined(status) ? status : CredentialStatus.Failed;
        }
        catch (OperationCanceledException) { return CredentialStatus.Cancelled; }
        catch (Exception) { return CredentialStatus.Failed; }
        finally
        {
            if (entered) _gate.Release();
        }
    }

    private string Target(string credentialName)
    {
        ValidateName(credentialName, nameof(credentialName));
        return _prefix + credentialName;
    }

    private static void ValidateName(string name, string parameterName)
    {
        LocalDataPaths.ValidateSingleSegment(name, parameterName);
        if (name.Length > 128) throw new ArgumentException("Credential namespace segments must not exceed 128 characters.", parameterName);
    }

    private sealed class NativeCredentialApi : IWindowsCredentialApi
    {
        private const uint GenericType = 1;
        private const uint LocalMachinePersistence = 2;
        private const int NotFoundError = 1168;
        public bool IsSupported => OperatingSystem.IsWindows();

        public CredentialStatus Write(string targetName, CredentialSecret secret)
        {
            if (!IsSupported) return CredentialStatus.Unsupported;
            var bytes = secret.CopyBytes();
            var blob = IntPtr.Zero;
            try
            {
                blob = Marshal.AllocHGlobal(bytes.Length);
                Marshal.Copy(bytes, 0, blob, bytes.Length);
                var credential = new NativeCredential
                {
                    Flags = 0,
                    Type = GenericType,
                    TargetName = targetName,
                    Comment = null,
                    LastWritten = default,
                    CredentialBlobSize = (uint)bytes.Length,
                    CredentialBlob = blob,
                    Persist = LocalMachinePersistence,
                    AttributeCount = 0,
                    Attributes = IntPtr.Zero,
                    TargetAlias = null,
                    UserName = null
                };
                return CredWrite(ref credential, 0) ? CredentialStatus.Succeeded : CredentialStatus.Failed;
            }
            finally
            {
                if (blob != IntPtr.Zero)
                {
                    ClearNative(blob, bytes.Length);
                    Marshal.FreeHGlobal(blob);
                }
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public CredentialReadResult Read(string targetName)
        {
            if (!IsSupported) return new CredentialReadResult(CredentialStatus.Unsupported);
            if (!CredRead(targetName, GenericType, 0, out var pointer))
                return new CredentialReadResult(Marshal.GetLastWin32Error() == NotFoundError ? CredentialStatus.NotFound : CredentialStatus.Failed);
            byte[]? bytes = null;
            IntPtr blob = IntPtr.Zero;
            var blobSize = 0;
            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                if (credential.CredentialBlobSize is < 1 or > CredentialSecret.MaximumBytes || credential.CredentialBlob == IntPtr.Zero)
                    return new CredentialReadResult(CredentialStatus.Failed);
                blob = credential.CredentialBlob;
                blobSize = (int)credential.CredentialBlobSize;
                bytes = new byte[blobSize];
                Marshal.Copy(blob, bytes, 0, blobSize);
                return new CredentialReadResult(CredentialStatus.Succeeded, new CredentialSecret(bytes));
            }
            finally
            {
                if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
                if (blob != IntPtr.Zero) ClearNative(blob, blobSize);
                CredFree(pointer);
            }
        }

        public CredentialStatus Delete(string targetName)
        {
            if (!IsSupported) return CredentialStatus.Unsupported;
            return CredDelete(targetName, GenericType, 0) ? CredentialStatus.Succeeded :
                Marshal.GetLastWin32Error() == NotFoundError ? CredentialStatus.NotFound : CredentialStatus.Failed;
        }

        private static void ClearNative(IntPtr pointer, int length)
        {
            for (var index = 0; index < length; index++) Marshal.WriteByte(pointer, index, 0);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NativeCredential
        {
            public uint Flags;
            public uint Type;
            public string? TargetName;
            public string? Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string? UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll")]
        private static extern void CredFree(IntPtr buffer);
    }
}
