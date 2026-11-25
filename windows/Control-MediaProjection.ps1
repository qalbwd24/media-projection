param(
    [ValidateSet('status','version','show','hide','reconnect','dock on','dock off','dock taskbar','dock move','dock reset','autostart on','autostart off',
        'dock background 0','dock background 20','dock background 45',
        'previous','next','play-pause','volume-up','volume-down','progress refresh','seek','exit')]
    [string]$Operation = 'status',
    [int]$PositionMs = -1
)

$ErrorActionPreference = 'Stop'
if ($Operation -eq 'seek') {
    if ($PositionMs -lt 0) { throw 'seek requires a nonnegative PositionMs' }
    $Operation = "seek $PositionMs"
}
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$pipe = [IO.Pipes.NamedPipeClientStream]::new('.', "MediaProjectionTool.$sid", [IO.Pipes.PipeDirection]::InOut,
    [IO.Pipes.PipeOptions]::Asynchronous)
try {
    $pipe.Connect(2200)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Operation + "`n")
    $pipe.Write($bytes, 0, $bytes.Length)
    $pipe.Flush()
    $reader = [IO.StreamReader]::new($pipe, [Text.Encoding]::UTF8, $false, 1024, $true)
    try {
        $reply = $reader.ReadLineAsync()
        if (!$reply.Wait(4000) -or [string]::IsNullOrEmpty($reply.Result)) { throw 'Media Projection did not reply.' }
        $reply.Result
        if (!(ConvertFrom-Json -InputObject $reply.Result).ok) { exit 1 }
    } finally { $reader.Dispose() }
} finally { $pipe.Dispose() }
