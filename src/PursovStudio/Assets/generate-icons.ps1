$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore,WindowsBase
$shapes = @(
  'M15,15 L24,6 C29,1 37,1 42,6 C47,11 47,19 42,24 L33,33 L27,27 L36,18 C38,16 38,14 36,12 C34,10 32,10 30,12 L21,21 Z',
  'M33,33 L24,42 C19,47 11,47 6,42 C1,37 1,29 6,24 L15,15 L21,21 L12,30 C10,32 10,34 12,36 C14,38 16,38 18,36 L27,27 Z',
  'M17,25 L25,17 L31,23 L23,31 Z'
)
$sizes = @(16,20,24,32,40,48,64,256)
foreach ($state in @('App','off','on','working','attention')) {
  $frames = @()
  foreach ($size in $sizes) {
    $visual = [System.Windows.Media.DrawingVisual]::new()
    $drawing = $visual.RenderOpen()
    $scale = $size / $(if ($state -eq 'App') { 48.0 } else { 64.0 })
    $drawing.PushTransform([System.Windows.Media.ScaleTransform]::new($scale,$scale))
    $colors = @('#19874B','#57C480','#239553')
    if ($state -eq 'off') { $colors = @('#747D77','#9AA59E','#747D77') }
    if ($state -eq 'working') { $colors = @('#236DAD','#73B5E8','#347DBB') }
    if ($state -eq 'attention') { $colors = @('#A77612','#E0B75C','#A77612') }
    for ($i=0; $i -lt $shapes.Count; $i++) {
      $brush = [System.Windows.Media.BrushConverter]::new().ConvertFromString($colors[$i])
      $drawing.DrawGeometry($brush,$null,[System.Windows.Media.Geometry]::Parse($shapes[$i]))
    }
    if ($state -ne 'App') {
      $badgeColor = switch ($state) { 'on' {'#176D3E'} 'off' {'#59645D'} 'working' {'#236DAD'} 'attention' {'#865C07'} }
      $badge = [System.Windows.Media.BrushConverter]::new().ConvertFromString($badgeColor)
      $drawing.DrawEllipse($badge,$null,[System.Windows.Point]::new(48,48),13,13)
      $pen = [System.Windows.Media.Pen]::new([System.Windows.Media.Brushes]::White,3.3)
      $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'
      $glyph = switch ($state) { 'on' {'M41,48 L46,53 L55,43'} 'off' {'M41,48 L55,48'} 'working' {'M42,43 L42,53 M49,43 L49,53 M56,43 L56,53'} 'attention' {'M48,40 L48,49 M48,55 L48,55.1'} }
      $drawing.DrawGeometry($null,$pen,[System.Windows.Media.Geometry]::Parse($glyph))
    }
    $drawing.Pop(); $drawing.Close()
    $bitmap = [System.Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = [System.Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [System.IO.MemoryStream]::new()
    $encoder.Save($stream)
    $frames += ,$stream.ToArray()
    $stream.Dispose()
  }
  if ($frames.Count -ne 8 -or @($frames | Where-Object { $_.Length -lt 100 }).Count -ne 0) { throw 'Icon frames failed validation.' }
  $name = if ($state -eq 'App') { 'App.ico' } else { 'Tray-' + $state + '.ico' }
  $file = [System.IO.File]::Create((Join-Path $PSScriptRoot $name))
  $writer = [System.IO.BinaryWriter]::new($file)
  $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
  $offset = 6 + 16 * $sizes.Count
  for ($i=0; $i -lt $sizes.Count; $i++) {
    $dimension = if ($sizes[$i] -eq 256) {0} else {$sizes[$i]}
    $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
    $writer.Write([byte]0); $writer.Write([byte]0)
    $writer.Write([uint16]1); $writer.Write([uint16]32)
    $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
    $offset += $frames[$i].Length
  }
  foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
  $writer.Dispose()
  Write-Output $name
}
