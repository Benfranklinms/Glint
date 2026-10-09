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
Add-Type -AssemblyName System.Drawing
$colors = @(@('#0F2027','#2C5364'), @('#41295a','#2F0743'), @('#134E5E','#71B280'), @('#C33764','#1D2671'))
1..4 | ForEach-Object {
  $bmp = New-Object System.Drawing.Bitmap 1280, 800
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $c = $colors[$_ - 1]
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Rectangle 0,0,1280,800), ([System.Drawing.ColorTranslator]::FromHtml($c[0])), ([System.Drawing.ColorTranslator]::FromHtml($c[1])), 45
  $g.FillRectangle($br, 0, 0, 1280, 800)
  $font = New-Object System.Drawing.Font 'Segoe UI', 64, ([System.Drawing.FontStyle]::Bold)
  $g.DrawString("Screenshot $_", $font, [System.Drawing.Brushes]::White, 80, 320)
  $g.Dispose()
  $bmp.Save((Join-Path $shots "Screenshot 2026-10-0$_.png"), [System.Drawing.Imaging.ImageFormat]::Png)
  $bmp.Dispose()
}
Get-ChildItem -Recurse $dev, (Join-Path $home_ 'Documents') | Select-Object FullName
