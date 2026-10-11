Imports System.Diagnostics
Imports System.IO
Imports System.Runtime.CompilerServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

Namespace Scripting

    ''' <summary>Owns one persistent Julia process for a flowsheet, with serialized script execution.</summary>
    Public NotInheritable Class JuliaScriptRunner
        Implements IDisposable

        Private ReadOnly _runGate As New SemaphoreSlim(1, 1)
        Private Shared ReadOnly _flowsheetRunners As New ConditionalWeakTable(Of Interfaces.IFlowsheet, JuliaScriptRunner)
        Private ReadOnly _sync As New Object
        Private _session As JuliaSession
        Private _disposed As Boolean

        Public Shared Function ForFlowsheet(flowsheet As Interfaces.IFlowsheet) As JuliaScriptRunner
            Return _flowsheetRunners.GetValue(flowsheet, Function(owner) New JuliaScriptRunner())
        End Function

        Public Shared Function FindExecutable() As String
            Dim filename = If(OperatingSystem.IsWindows(), "julia.exe", "julia")
            For Each directory In If(Environment.GetEnvironmentVariable("PATH"), "").Split(Path.PathSeparator)
                If String.IsNullOrWhiteSpace(directory) Then Continue For
                Dim candidate = Path.Combine(directory.Trim(), filename)
                If File.Exists(candidate) Then Return Path.GetFullPath(candidate)
            Next

            Dim juliaup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".juliaup", "bin", filename)
            If File.Exists(juliaup) Then Return juliaup

            Throw New FileNotFoundException("Julia was not found. Install Julia with Juliaup or add it to PATH, then restart DWSIM.")
        End Function

        Public Sub Run(script As Interfaces.IScript, workingDirectory As String,
                       output As Action(Of String), shouldStop As Func(Of Boolean))
            RunSource(script.ScriptText, workingDirectory, output, shouldStop)
        End Sub

        Public Sub RunSource(source As String, workingDirectory As String,
                             output As Action(Of String), shouldStop As Func(Of Boolean))
            While Not _runGate.Wait(100)
                If shouldStop() Then Throw New OperationCanceledException()
            End While

            Try
                If shouldStop() Then Throw New OperationCanceledException()
                If Not Directory.Exists(workingDirectory) Then workingDirectory = Environment.CurrentDirectory

                ' Keep diagnostics for this run, even when the caller hides informational output.
                ' Bound the buffer so verbose scripts cannot grow the error message indefinitely.
                Const maxErrorLength As Integer = 32768
                Dim errorOutput As New StringBuilder()
                Dim errorOutputTruncated As Boolean = False
                Dim forwardOutput As Action(Of String) =
                    Sub(line)
                        If line.StartsWith("[stderr] ", StringComparison.Ordinal) Then
                            SyncLock errorOutput
                                errorOutput.AppendLine(line.Substring(9))
                                If errorOutput.Length > maxErrorLength Then
                                    errorOutput.Remove(0, errorOutput.Length - maxErrorLength)
                                    errorOutputTruncated = True
                                End If
                            End SyncLock
                        End If
                        output(line)
                    End Sub

                Dim session As JuliaSession
                Dim started As Boolean = False
                SyncLock _sync
                    If _disposed Then Throw New ObjectDisposedException(NameOf(JuliaScriptRunner))
                    If _session IsNot Nothing AndAlso _session.HasExited Then
                        _session.Dispose()
                        _session = Nothing
                    End If
                    If _session Is Nothing Then
                        _session = New JuliaSession(FindExecutable(), workingDirectory, forwardOutput)
                        started = True
                    End If
                    session = _session
                End SyncLock

                Dim succeeded As Boolean
                Try
                    output(If(started, "Starting Julia: " & session.Executable, "Reusing Julia session") &
                           " (PID " & session.ID.ToString() & ").")
                    succeeded = session.Run(source, workingDirectory, forwardOutput, shouldStop)
                Catch
                    Dim cancelled = shouldStop() OrElse session.IsStopped
                    SyncLock _sync
                        If _session Is session Then _session = Nothing
                    End SyncLock
                    session.Dispose()
                    If cancelled Then Throw New OperationCanceledException("Julia execution stopped; the session was reset.")
                    Throw
                End Try

                ' An ordinary script error leaves the process and its loaded packages available.
                If Not succeeded Then
                    Dim details As String
                    SyncLock errorOutput
                        details = errorOutput.ToString().Trim()
                        If errorOutputTruncated Then details = "[Earlier Julia stderr omitted.]" & Environment.NewLine & details
                    End SyncLock
                    Throw New InvalidOperationException(If(details.Length = 0,
                        "Julia script failed. See the script output for details.",
                        "Julia script failed:" & Environment.NewLine & details))
                End If
            Finally
                _runGate.Release()
            End Try
        End Sub

        Public Sub Dispose() Implements IDisposable.Dispose
            Dim session As JuliaSession
            SyncLock _sync
                If _disposed Then Return
                _disposed = True
                session = _session
                _session = Nothing
            End SyncLock
            session?.Dispose()
        End Sub

        Private NotInheritable Class JuliaSession
            Implements IDisposable

            Private ReadOnly _process As New Process()
            Private ReadOnly _token As String = "__DWSIM_JULIA_" & Guid.NewGuid().ToString("N")
            Private ReadOnly _outputLock As New Object
            Private ReadOnly _stdout As Task
            Private ReadOnly _stderr As Task
            Private _output As Action(Of String)
            Private _marker As String = ""
            Private _stdoutEnd As TaskCompletionSource(Of Boolean)
            Private _stderrEnd As TaskCompletionSource(Of Boolean)
            Private _stopped As Integer

            Public ReadOnly Property Executable As String
            Public ReadOnly Property ID As Integer
            Public ReadOnly Property IsStopped As Boolean
                Get
                    Return Volatile.Read(_stopped) <> 0
                End Get
            End Property
            Public ReadOnly Property HasExited As Boolean
                Get
                    Return IsStopped OrElse _process.HasExited
                End Get
            End Property

            Public Sub New(executable As String, workingDirectory As String, output As Action(Of String))
                Me.Executable = executable
                _output = output
                Try
                    Dim info As New ProcessStartInfo(executable) With {
                        .UseShellExecute = False,
                        .CreateNoWindow = True,
                        .WorkingDirectory = workingDirectory,
                        .RedirectStandardInput = True,
                        .RedirectStandardOutput = True,
                        .RedirectStandardError = True,
                        .StandardInputEncoding = New UTF8Encoding(False),
                        .StandardOutputEncoding = Encoding.UTF8,
                        .StandardErrorEncoding = Encoding.UTF8
                    }
                    info.ArgumentList.Add("--startup-file=no")
                    info.ArgumentList.Add("--history-file=no")
                    info.ArgumentList.Add("--color=no")
                    info.ArgumentList.Add("-e")
                    Using reader As New StreamReader(GetType(JuliaScriptRunner).Assembly.GetManifestResourceStream(
                            "DWSIM.SharedClasses.Scripting.JuliaWorker.jl"), Encoding.UTF8)
                        info.ArgumentList.Add(reader.ReadToEnd())
                    End Using
                    info.ArgumentList.Add(_token)
                    _process.StartInfo = info
                    _process.Start()
                    ID = _process.Id
                    _process.StandardInput.AutoFlush = True
                    _stdout = ForwardOutputAsync(_process.StandardOutput, False)
                    _stderr = ForwardOutputAsync(_process.StandardError, True)
                Catch
                    _process.Dispose()
                    Throw
                End Try
            End Sub

            Public Function Run(source As String, workingDirectory As String, output As Action(Of String),
                                shouldStop As Func(Of Boolean)) As Boolean
                Dim runID = Guid.NewGuid().ToString("N")
                Dim stdoutEnd As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                Dim stderrEnd As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
                SyncLock _outputLock
                    _output = output
                    _marker = _token & ":" & runID & ":"
                    _stdoutEnd = stdoutEnd
                    _stderrEnd = stderrEnd
                End SyncLock

                Try
                    Dim filename = Path.Combine(workingDirectory, "dwsim-script.jl")
                    Dim request = String.Join(vbTab, runID, Encode(workingDirectory), Encode(filename), Encode(source))
                    WaitFor(_process.StandardInput.WriteLineAsync(request), shouldStop)
                    WaitFor(Task.WhenAll(stdoutEnd.Task, stderrEnd.Task), shouldStop)
                    Return stdoutEnd.Task.Result AndAlso stderrEnd.Task.Result
                Finally
                    SyncLock _outputLock
                        _output = Nothing
                        _marker = ""
                    End SyncLock
                End Try
            End Function

            Private Shared Function Encode(value As String) As String
                Return Convert.ToBase64String(Encoding.UTF8.GetBytes(If(value, "")))
            End Function

            Private Sub WaitFor(pending As Task, shouldStop As Func(Of Boolean))
                While Not pending.IsCompleted
                    If shouldStop() OrElse IsStopped Then Throw New OperationCanceledException()
                    If _process.WaitForExit(100) Then
                        Throw New InvalidOperationException("Julia session exited with code " & _process.ExitCode.ToString() &
                                                            ". Run again to start a new session.")
                    End If
                    If _stdout.IsCompleted OrElse _stderr.IsCompleted Then
                        If _stdout.IsFaulted Then _stdout.GetAwaiter().GetResult()
                        If _stderr.IsFaulted Then _stderr.GetAwaiter().GetResult()
                        Throw New IOException("Julia's output channel closed. Run again to start a new session.")
                    End If
                End While
                If shouldStop() OrElse IsStopped Then Throw New OperationCanceledException()
                pending.GetAwaiter().GetResult()
            End Sub

            Private Async Function ForwardOutputAsync(reader As StreamReader, isError As Boolean) As Task
                Try
                    While True
                        Dim line = Await reader.ReadLineAsync().ConfigureAwait(False)
                        If line Is Nothing Then Exit While
                        SyncLock _outputLock
                            Dim index = If(_marker.Length = 0, -1, line.IndexOf(_marker, StringComparison.Ordinal))
                            If index >= 0 Then
                                Dim status = line.Substring(index + _marker.Length)
                                If status = "OK" OrElse status = "ERROR" Then
                                    ' print() need not end with a newline; keep the text before the marker.
                                    If index > 0 Then _output?.Invoke(If(isError, "[stderr] ", "") & line.Substring(0, index))
                                    Dim completion = If(isError, _stderrEnd, _stdoutEnd)
                                    completion.TrySetResult(status = "OK")
                                    Continue While
                                End If
                            End If
                            _output?.Invoke(If(isError, "[stderr] ", "") & line)
                        End SyncLock
                    End While
                Catch ex As Exception When IsStopped
                    ' Closing the flowsheet or pressing Stop can close a pipe while it is being read.
                End Try
            End Function

            Public Sub Dispose() Implements IDisposable.Dispose
                If Interlocked.Exchange(_stopped, 1) <> 0 Then Return
                Try
                    If Not _process.HasExited Then _process.Kill(entireProcessTree:=True)
                Catch ex As InvalidOperationException
                Catch ex As System.ComponentModel.Win32Exception
                Finally
                    ' Pipe readers finish after process termination; do not wait on the UI thread.
                    Task.WhenAll(_stdout, _stderr).ContinueWith(
                        Sub(completed)
                            Dim ignored = completed.Exception
                            _process.Dispose()
                        End Sub, TaskScheduler.Default)
                End Try
            End Sub
        End Class
    End Class
End Namespace
