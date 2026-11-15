# Downloads the whisper.cpp "base" speech model used for dictation (~141 MB).
# It is not stored in git because GitHub rejects files over 100 MB.
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\assets\speech\ggml-base.bin'
if (Test-Path $target) { Write-Host "Model already present: $target"; exit 0 }
$url = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin'
Write-Host "Downloading $url ..."
Invoke-WebRequest -Uri $url -OutFile "$target.part" -UseBasicParsing
Move-Item "$target.part" $target -Force
Write-Host "Saved to $target"
