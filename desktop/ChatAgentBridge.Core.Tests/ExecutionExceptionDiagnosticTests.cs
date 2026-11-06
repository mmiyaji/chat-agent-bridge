using System.ComponentModel;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Text.Json;
using ChatAgentBridge.Core;

namespace ChatAgentBridge.Core.Tests;

public sealed class ExecutionExceptionDiagnosticTests
{
    public static TheoryData<Exception, ExecutionExceptionCause> Causes => new()
    {
        { new UnauthorizedAccessException(), ExecutionExceptionCause.AccessDenied },
        { new SecurityException(), ExecutionExceptionCause.AccessDenied },
        { new DirectoryNotFoundException(), ExecutionExceptionCause.WorkspaceUnavailable },
        { new FileNotFoundException(), ExecutionExceptionCause.RequiredFileUnavailable },
        { new InvalidDataException(), ExecutionExceptionCause.InvalidData },
        { new JsonException(), ExecutionExceptionCause.InvalidData },
        { new SocketException(), ExecutionExceptionCause.Network },
        { new HttpRequestException(), ExecutionExceptionCause.Network },
        { new TimeoutException(), ExecutionExceptionCause.Timeout },
        { new Win32Exception(2), ExecutionExceptionCause.ProcessStart },
        { new IOException(), ExecutionExceptionCause.LocalStorage },
        { new ArgumentException(), ExecutionExceptionCause.InvalidSettings },
        { new InvalidOperationException(), ExecutionExceptionCause.InvalidState },
        { new Exception(), ExecutionExceptionCause.Unknown }
    };

    [Theory]
    [MemberData(nameof(Causes))]
    public void ClassifiesWrappedRootWithoutLosingDiagnosticCode(Exception error, ExecutionExceptionCause cause)
    {
        var result = ExecutionExceptionDiagnostic.Create(new Exception("outer secret", error));
        Assert.Equal(cause, result.Cause);
        Assert.Equal(error.GetType().Name, result.ExceptionType);
        Assert.Equal(unchecked((uint)error.HResult).ToString("X8"), result.HResult);
    }

    [Fact]
    public void DoesNotExposeMessagePathPromptTokenOrStackTrace()
    {
        const string secret = "C:\\private\\prompt.txt Bearer secret-token user-prompt";
        Exception error;
        try { throw new IOException(secret); }
        catch (Exception caught) { error = caught; }
        var result = ExecutionExceptionDiagnostic.Create(error);
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(secret, serialized);
        Assert.DoesNotContain("private", serialized);
        Assert.DoesNotContain(nameof(DoesNotExposeMessagePathPromptTokenOrStackTrace), serialized);
        Assert.Equal("IOException", result.ExceptionType);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("running", true)]
    [InlineData("failed", false)]
    [InlineData("completed", false)]
    [InlineData("cancelled", false)]
    [InlineData("interrupted", false)]
    public void PreservesTerminalExecutionStatus(string? status, bool expected) =>
        Assert.Equal(expected, ExecutionExceptionDiagnostic.ShouldMarkExecutionError(status));
}
