# Deploys ApTutor.ContentAdmin to AWS App Runner: builds the Docker image, pushes it to ECR, starts
# an App Runner deployment, then polls until it finishes. Run from anywhere — it resolves the repo
# root itself (the Docker build context and Dockerfile path both need to be relative to the repo
# root, not wherever you happen to be when you run this).
#
# Usage (from PowerShell):
#   .\scripts\deploy-content-admin.ps1
#   .\scripts\deploy-content-admin.ps1 -Profile deploy -Region us-east-1
#
# Requires: docker (logged in / Docker Desktop running), aws CLI configured with the named profile.

param(
    [string]$Region = "us-east-1",
    [string]$Profile = "deploy",
    [string]$AccountId = "458013564402",
    [string]$Repo = "tutor-ai-content-admin",
    [string]$ServiceArn = "arn:aws:apprunner:us-east-1:458013564402:service/tutor-ai-content-admin/3562757ad0cf405a8480d293bcd104df"
)

$ErrorActionPreference = "Stop"

# Repo root is one level up from this script's own folder (scripts/deploy-content-admin.ps1).
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Push-Location $repoRoot
try {
    $registry = "$AccountId.dkr.ecr.$Region.amazonaws.com"
    $localTag = "${Repo}:latest"
    $remoteTag = "$registry/${Repo}:latest"

    Write-Host "==> Logging into ECR ($registry)..." -ForegroundColor Cyan
    aws ecr get-login-password --region $Region --profile $Profile | docker login --username AWS --password-stdin $registry
    if ($LASTEXITCODE -ne 0) { throw "docker login failed." }

    Write-Host "==> Building image ($localTag)..." -ForegroundColor Cyan
    docker build --platform linux/amd64 -f src/ApTutor.ContentAdmin/Dockerfile -t $localTag .
    if ($LASTEXITCODE -ne 0) { throw "docker build failed." }

    Write-Host "==> Tagging and pushing ($remoteTag)..." -ForegroundColor Cyan
    docker tag $localTag $remoteTag
    docker push $remoteTag
    if ($LASTEXITCODE -ne 0) { throw "docker push failed." }

    Write-Host "==> Starting App Runner deployment..." -ForegroundColor Cyan
    aws apprunner start-deployment --service-arn $ServiceArn --region $Region --profile $Profile | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "aws apprunner start-deployment failed." }

    Write-Host "==> Waiting for deployment to finish (checking every 15s)..." -ForegroundColor Cyan
    do {
        Start-Sleep -Seconds 15
        $operations = aws apprunner list-operations --service-arn $ServiceArn --region $Region --profile $Profile --max-results 1 | ConvertFrom-Json
        $status = $operations.OperationSummaryList[0].Status
        Write-Host "    status: $status"
    } while ($status -eq "IN_PROGRESS" -or $status -eq "PENDING")

    if ($status -eq "SUCCEEDED") {
        Write-Host "==> Deployment succeeded." -ForegroundColor Green
    } else {
        Write-Host "==> Deployment ended with status: $status" -ForegroundColor Red
        exit 1
    }
} finally {
    Pop-Location
}
