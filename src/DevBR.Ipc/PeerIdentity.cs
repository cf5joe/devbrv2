using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace DevBR.Ipc;

/// <summary>Facts about the process on the other end of a pipe, read from the OS rather than from the peer.</summary>
public sealed record PeerInfo(int ProcessId, string? ImagePath, SecurityIdentifier? UserSid);

public interface IPeerPolicy
{
    bool IsAllowed(PeerInfo peer, out string reason);
}

/// <summary>Accepts exactly one expected process, optionally pinned to an executable path and user.</summary>
public sealed class ExpectedPeerPolicy(int expectedProcessId, string? expectedImagePath = null, SecurityIdentifier? expectedUser = null) : IPeerPolicy
{
    public bool IsAllowed(PeerInfo peer, out string reason)
    {
        if (peer.ProcessId != expectedProcessId)
        {
            reason = $"unexpected process {peer.ProcessId} (expected {expectedProcessId})";
            return false;
        }

        if (expectedImagePath is not null &&
            !string.Equals(Path.GetFullPath(expectedImagePath), peer.ImagePath, StringComparison.OrdinalIgnoreCase))
        {
            reason = $"unexpected executable '{peer.ImagePath}'";
            return false;
        }

        if (expectedUser is not null && peer.UserSid != expectedUser)
        {
            reason = "unexpected user account";
            return false;
        }

        reason = string.Empty;
        return true;
    }
}

public static partial class PeerInspector
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;

    public static PeerInfo DescribeClient(NamedPipeServerStream pipe)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return Describe((int)pid);
    }

    public static PeerInfo DescribeServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return Describe((int)pid);
    }

    public static PeerInfo Describe(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process.IsInvalid)
        {
            return new PeerInfo(processId, null, null);
        }

        return new PeerInfo(processId, QueryImagePath(process), QueryUser(process));
    }

    private static string? QueryImagePath(SafeProcessHandle process)
    {
        var buffer = new char[32768];
        var size = (uint)buffer.Length;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
    }

    private static SecurityIdentifier? QueryUser(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, TokenQuery, out var token))
        {
            return null;
        }

        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            return identity.User;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, [Out] char[] exeName, ref uint size);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);
}

public static class PipeSecurityFactory
{
    /// <summary>
    /// Grants read/write only to the listed accounts and full control to the creating account; denies all
    /// network logons so the pipe is local-only.
    /// </summary>
    public static PipeSecurity Create(params SecurityIdentifier[] allowedUsers)
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        using var current = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(current.User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        foreach (var user in allowedUsers.Where(u => u != current.User))
        {
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        }

        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return security;
    }

    /// <summary>Random, unguessable pipe name for one session.</summary>
    public static string NewPipeName(string role) => $"DevBR.{role}.{Environment.ProcessId}.{Guid.NewGuid():N}";
}
