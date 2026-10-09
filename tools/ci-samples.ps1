# Lays out a small, realistic user folder on the CI runner for demo screenshots.
$ErrorActionPreference = 'Continue'
$home_ = $env:USERPROFILE
$dev = Join-Path $home_ 'Developer\fsearch'
$college = Join-Path $home_ 'Documents\College'
$shots = Join-Path $home_ 'Pictures\Screenshots'
New-Item -ItemType Directory -Force (Join-Path $dev 'src'), $college, $shots | Out-Null
@'
fn main() {
    // TODO: wire up the daemon
    let dir = apply_dir("~");
}
'@ | Set-Content (Join-Path $dev 'src\main.rs')
@'
pub fn apply_dir(p: &str) -> String {
    // TODO: handle symlinks
    p.to_string()
}
'@ | Set-Content (Join-Path $dev 'src\index.rs')
"[package]`nname = `"fsearch`"" | Set-Content (Join-Path $dev 'Cargo.toml')
'# fsearch' | Set-Content (Join-Path $dev 'README.md')
'Maintenance log' | Set-Content (Join-Path $college 'maintenance-notes.md')
$samples = Join-Path $PSScriptRoot 'samples'
Copy-Item (Join-Path $samples 'DBMS-lab-report.pdf'), (Join-Path $samples 'placement-notes.docx') $college
Copy-Item (Join-Path $samples 'resume-final-v3.pdf') (Join-Path $home_ 'Documents')
1..4 | ForEach-Object { [IO.File]::WriteAllBytes((Join-Path $shots "Screenshot 2026-10-0$_.png"), (New-Object byte[] (2MB))) }
Get-ChildItem -Recurse $dev, (Join-Path $home_ 'Documents') | Select-Object FullName
