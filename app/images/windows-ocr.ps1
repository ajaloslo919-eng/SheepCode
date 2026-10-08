$ErrorActionPreference = 'Stop'
[Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
$taskBitmap = $null
$taskStream = $null
try {
    # The command is bundled by SheepCode. The only input is JSON data, never script.
    $taskInput = [Console]::ReadLine()
    if (!$taskInput -or $taskInput.Length -gt 8192) { throw 'Invalid OCR request.' }
    $taskRequest = $taskInput | ConvertFrom-Json
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
    $taskLanguages = @([Windows.Media.Ocr.OcrEngine]::AvailableRecognizerLanguages | ForEach-Object { $_.LanguageTag })
    if ($taskRequest.action -eq 'status') {
        $taskReply = @{ status = $(if ($taskLanguages.Count) { 'available' } else { 'unavailable' }); languages = $taskLanguages; maxDimension = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension; error = $(if ($taskLanguages.Count) { '' } else { 'No Windows OCR language is installed.' }) }
    } elseif ($taskRequest.action -eq 'read') {
        $taskAwaitMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.IsGenericMethodDefinition -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
        function Wait-ImageOperation($operation, [Type]$resultType) {
            $taskAwait = $taskAwaitMethod.MakeGenericMethod($resultType).Invoke($null, @($operation))
            $taskAwait.Wait()
            return $taskAwait.Result
        }
        $null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
        $null = [Windows.Storage.Streams.IRandomAccessStreamWithContentType, Windows.Storage.Streams, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
        $null = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics.Imaging, ContentType = WindowsRuntime]
        $null = [Windows.Globalization.Language, Windows.Globalization, ContentType = WindowsRuntime]
        if ($taskRequest.language -and $taskRequest.language -ne 'auto') {
            $taskLanguage = New-Object Windows.Globalization.Language($taskRequest.language)
            $taskEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage($taskLanguage)
        } else {
            $taskEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
            if (!$taskEngine -and $taskLanguages.Count) { $taskEngine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage((New-Object Windows.Globalization.Language($taskLanguages[0]))) }
        }
        if (!$taskEngine) {
            $taskReply = @{ status = 'unavailable'; languages = $taskLanguages; error = 'The requested OCR language is not installed in Windows.'; text = '' }
        } else {
            $taskFile = Wait-ImageOperation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($taskRequest.path)) ([Windows.Storage.StorageFile])
            $taskStream = Wait-ImageOperation ($taskFile.OpenReadAsync()) ([Windows.Storage.Streams.IRandomAccessStreamWithContentType])
            $taskDecoder = Wait-ImageOperation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($taskStream)) ([Windows.Graphics.Imaging.BitmapDecoder])
            if ([long]$taskDecoder.PixelWidth * [long]$taskDecoder.PixelHeight -gt 16000000 -or $taskDecoder.PixelWidth -gt 8192 -or $taskDecoder.PixelHeight -gt 8192) { throw 'Image exceeds the pixel limit.' }
            $taskMaximum = [Windows.Media.Ocr.OcrEngine]::MaxImageDimension
            $taskScale = [Math]::Min(1, $taskMaximum / [double][Math]::Max($taskDecoder.PixelWidth, $taskDecoder.PixelHeight))
            $taskTransform = New-Object Windows.Graphics.Imaging.BitmapTransform
            $taskTransform.ScaledWidth = [uint32][Math]::Max(1, [Math]::Round($taskDecoder.PixelWidth * $taskScale))
            $taskTransform.ScaledHeight = [uint32][Math]::Max(1, [Math]::Round($taskDecoder.PixelHeight * $taskScale))
            $taskBitmap = Wait-ImageOperation ($taskDecoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore, $taskTransform, [Windows.Graphics.Imaging.ExifOrientationMode]::IgnoreExifOrientation, [Windows.Graphics.Imaging.ColorManagementMode]::DoNotColorManage)) ([Windows.Graphics.Imaging.SoftwareBitmap])
            $taskResult = Wait-ImageOperation ($taskEngine.RecognizeAsync($taskBitmap)) ([Windows.Media.Ocr.OcrResult])
            $taskText = ($taskResult.Lines | ForEach-Object { $_.Text }) -join "`n"
            $taskReply = @{ status = $(if ($taskText.Length) { 'read' } else { 'no_text' }); text = $taskText.Substring(0, [Math]::Min(30000, $taskText.Length)); totalCharacters = $taskText.Length; truncated = ($taskText.Length -gt 30000); language = $taskEngine.RecognizerLanguage.LanguageTag; ocrWidth = $taskTransform.ScaledWidth; ocrHeight = $taskTransform.ScaledHeight; error = '' }
        }
    } else { throw 'Unknown OCR operation.' }
} catch {
    $taskReply = @{ status = 'unavailable'; text = ''; languages = @(); error = $_.Exception.GetBaseException().Message }
} finally {
    if ($taskBitmap) { $taskBitmap.Dispose() }
    if ($taskStream) { $taskStream.Dispose() }
}
[Console]::WriteLine(($taskReply | ConvertTo-Json -Depth 6 -Compress))
