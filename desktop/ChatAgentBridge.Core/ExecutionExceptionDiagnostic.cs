using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;

namespace ChatAgentBridge.Core;

/// <summary>Safe, non-persistent details for an unexpected exception during a chat execution.</summary>
public sealed record ExecutionExceptionDiagnostic(
    ExecutionExceptionCause Cause,
    string ExceptionType,
    string HResult)
{
    /// <summary>
    /// Extracts only an allowlisted cause, a bounded exception type name, and HRESULT.
    /// Exception messages, stack traces, paths, command arguments, prompts, and tokens are excluded.
    /// </summary>
    public static ExecutionExceptionDiagnostic Create(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var root = exception.GetBaseException();
        return new(Classify(root), SafeTypeName(root.GetType().Name),
            unchecked((uint)root.HResult).ToString("X8", CultureInfo.InvariantCulture));
    }

    /// <summary>Only an unrecorded or still-running execution should change to the CAB exception state.</summary>
    public static bool ShouldMarkExecutionError(string? executionStatus) =>
        executionStatus is null or "running";

    private static ExecutionExceptionCause Classify(Exception exception) => exception switch
    {
        UnauthorizedAccessException or SecurityException => ExecutionExceptionCause.AccessDenied,
        DirectoryNotFoundException => ExecutionExceptionCause.WorkspaceUnavailable,
        FileNotFoundException => ExecutionExceptionCause.RequiredFileUnavailable,
        InvalidDataException or JsonException => ExecutionExceptionCause.InvalidData,
        SocketException or HttpRequestException => ExecutionExceptionCause.Network,
        TimeoutException => ExecutionExceptionCause.Timeout,
        Win32Exception => ExecutionExceptionCause.ProcessStart,
        IOException => ExecutionExceptionCause.LocalStorage,
        ArgumentException => ExecutionExceptionCause.InvalidSettings,
        InvalidOperationException => ExecutionExceptionCause.InvalidState,
        _ => ExecutionExceptionCause.Unknown
    };

    private static string SafeTypeName(string name)
    {
        if (name.Length is < 1 or > 64 || !IsAsciiLetter(name[0])) return "Exception";
        foreach (var character in name)
            if (!IsAsciiLetter(character) && !char.IsAsciiDigit(character) && character != '_')
                return "Exception";
        return name;
    }

    private static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
}

public enum ExecutionExceptionCause
{
    AccessDenied,
    WorkspaceUnavailable,
    RequiredFileUnavailable,
    InvalidData,
    Network,
    Timeout,
    ProcessStart,
    LocalStorage,
    InvalidSettings,
    InvalidState,
    Unknown
}
