[CmdletBinding()]
param([Parameter(Mandatory)][ValidateRange(1, 2147483647)][int] $ProcessId)

$ErrorActionPreference = 'Stop'
if (-not ('NotificationNativeProbe.PackageDiagnostic.ProcessPackage' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace NotificationNativeProbe.PackageDiagnostic {
    public static class ProcessPackage {
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        private static extern int GetPackageFullName(SafeProcessHandle process, ref uint count, StringBuilder name);
        public static string Read(int processId) {
            using (var process = OpenProcess(0x1000, false, processId)) { // PROCESS_QUERY_LIMITED_INFORMATION
                if (process.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                uint count = 0;
                int result = GetPackageFullName(process, ref count, null);
                if (result == 15700) return null; // APPMODEL_ERROR_NO_PACKAGE
                if (result != 122) throw new Win32Exception(result); // ERROR_INSUFFICIENT_BUFFER
                var name = new StringBuilder(checked((int)count));
                result = GetPackageFullName(process, ref count, name);
                if (result != 0) throw new Win32Exception(result);
                return name.ToString();
            }
        }
    }
}
'@
}
$probeProcess = Get-Process -Id $ProcessId -ErrorAction Stop
$packageFullName = [NotificationNativeProbe.PackageDiagnostic.ProcessPackage]::Read($ProcessId)
[pscustomobject]@{
    processId = $ProcessId
    executable = $probeProcess.Path
    hasPackageIdentity = $null -ne $packageFullName
    packageFullName = $packageFullName
} | ConvertTo-Json
