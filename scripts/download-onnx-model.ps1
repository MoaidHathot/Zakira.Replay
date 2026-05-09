param(
    [string]$OutputDirectory = "models\all-MiniLM-L6-v2",
    [ValidateSet("model_quantized.onnx", "model_int8.onnx", "model.onnx", "model_fp16.onnx")]
    [string]$ModelFile = "model_quantized.onnx",
    [switch]$Configure
)

$ErrorActionPreference = "Stop"

$repo = "https://huggingface.co/Xenova/all-MiniLM-L6-v2/resolve/main"
$files = @(
    @{ Name = "model.onnx"; Url = "$repo/onnx/$($ModelFile)?download=true" },
    @{ Name = "vocab.txt"; Url = "$repo/vocab.txt?download=true" },
    @{ Name = "config.json"; Url = "$repo/config.json?download=true" },
    @{ Name = "tokenizer_config.json"; Url = "$repo/tokenizer_config.json?download=true" }
)

$target = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $target -Force | Out-Null

foreach ($file in $files) {
    $path = Join-Path $target $file.Name
    if (Test-Path -LiteralPath $path) {
        "Exists: $path"
        continue
    }

    "Downloading $($file.Url)"
    Invoke-WebRequest -Uri $file.Url -OutFile $path
    "Wrote: $path"
}

$modelPath = Join-Path $target "model.onnx"
$vocabPath = Join-Path $target "vocab.txt"

""
"ONNX model: $modelPath"
"Vocabulary: $vocabPath"
""
"Use with:"
"  zakira-replay search build runs\example-run --backend sqlite-onnx --onnx-model `"$modelPath`" --onnx-vocab `"$vocabPath`""

if ($Configure) {
    dotnet run --project "src\Zakira.Replay\Zakira.Replay.csproj" -- config set search.onnx.modelPath $modelPath
    dotnet run --project "src\Zakira.Replay\Zakira.Replay.csproj" -- config set search.onnx.vocabularyPath $vocabPath
    "Configured Zakira.Replay ONNX search paths."
}
