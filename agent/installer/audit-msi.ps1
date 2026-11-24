param([Parameter(Mandatory = $true)][string]$MsiPath)

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $installer.GetType().InvokeMember("OpenDatabase", [System.Reflection.BindingFlags]::InvokeMethod, $null, $installer, @($MsiPath, 0))

function Invoke-Msi([string]$Sql)
{
    $view = $database.GetType().InvokeMember("OpenView", [System.Reflection.BindingFlags]::InvokeMethod, $null, $database, @($Sql))
    $view.GetType().InvokeMember("Execute", [System.Reflection.BindingFlags]::InvokeMethod, $null, $view, $null)
    while ($true)
    {
        $record = $view.GetType().InvokeMember("Fetch", [System.Reflection.BindingFlags]::InvokeMethod, $null, $view, $null)
        if ($null -eq $record) { break }
        $count = $record.GetType().InvokeMember("FieldCount", [System.Reflection.BindingFlags]::GetProperty, $null, $record, $null)
        $values = @()
        for ($i = 1; $i -le $count; $i++)
        {
            $value = $record.GetType().InvokeMember("StringData", [System.Reflection.BindingFlags]::GetProperty, $null, $record, @($i))
            $values += [string]$value
        }
        Write-Host ("  " + ($values -join " | "))
    }
    $view.GetType().InvokeMember("Close", [System.Reflection.BindingFlags]::InvokeMethod, $null, $view, $null)
}

Write-Host "== ServiceInstall =="
Invoke-Msi "SELECT * FROM ``ServiceInstall``"

Write-Host "== ServiceControl =="
Invoke-Msi "SELECT * FROM ``ServiceControl``"

Write-Host "== Environment =="
Invoke-Msi "SELECT * FROM ``Environment``"

Write-Host "== MsiLockPermissionsEx (ACL de los datos, S-01) =="
Invoke-Msi "SELECT * FROM ``MsiLockPermissionsEx``"

Write-Host "== Upgrade =="
Invoke-Msi "SELECT * FROM ``Upgrade``"
