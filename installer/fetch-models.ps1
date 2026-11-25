<#
.SYNOPSIS
  Downloads the two open-weight ONNX models the local runtime embeds (bge-small-en-v1.5 for embeddings,
  the MiniLM cross-encoder for reranking) so the installer can bundle them. Run once per machine that
  builds the installer; the result is cached and skipped on later runs.

.DESCRIPTION
  These are the same models services/model-service/app.py loads through fastembed, fetched here straight
  from their public Hugging Face repositories - open weights, no account and no paid API, consistent with
  "no paid third-party services" (see project memory). This script never runs on an end user's machine:
  the installer bundles the files it downloads, so installed Knovara needs no network for ingestion or search.

.PARAMETER Destination
  Where to put them (default: the folder the local runtime reads from when no override is configured,
  see OnnxModelOptions.ModelDirectory - keeping them in sync means a model fetched here also works for
  anyone running the runtime directly from a checkout, without an installer).
#>
param(
    [string] $Destination = (Join-Path $env:LOCALAPPDATA 'Knovara\models')
)

$ErrorActionPreference = 'Stop'

$files = @(
    @{ Dir = 'bge-small';     Name = 'model.onnx';     MinBytes = 10MB; Url = 'https://huggingface.co/Qdrant/bge-small-en-v1.5-onnx-Q/resolve/main/model_optimized.onnx' }
    @{ Dir = 'bge-small';     Name = 'tokenizer.json';  MinBytes = 100KB; Url = 'https://huggingface.co/Qdrant/bge-small-en-v1.5-onnx-Q/resolve/main/tokenizer.json' }
    @{ Dir = 'minilm-rerank'; Name = 'model.onnx';     MinBytes = 10MB; Url = 'https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/onnx/model.onnx' }
    @{ Dir = 'minilm-rerank'; Name = 'tokenizer.json';  MinBytes = 100KB; Url = 'https://huggingface.co/Xenova/ms-marco-MiniLM-L-6-v2/resolve/main/tokenizer.json' }
)

foreach ($file in $files) {
    $dir = Join-Path $Destination $file.Dir
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $target = Join-Path $dir $file.Name

    if ((Test-Path $target) -and (Get-Item $target).Length -ge $file.MinBytes) {
        Write-Host "Already have $($file.Dir)/$($file.Name)"
        continue
    }

    Write-Host "Downloading $($file.Dir)/$($file.Name) ..."
    Invoke-WebRequest -Uri $file.Url -OutFile $target -UseBasicParsing
    if ((Get-Item $target).Length -lt $file.MinBytes) { throw "Downloaded file is smaller than expected: $target" }
}

Write-Host "Models are ready at $Destination"
