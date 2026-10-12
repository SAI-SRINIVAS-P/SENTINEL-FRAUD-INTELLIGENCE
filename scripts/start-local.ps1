$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath '.env')) {
    throw 'Missing .env. This ZIP includes .env; extract all files and make sure the hidden-dot filename was copied.'
}

$envText = Get-Content -LiteralPath '.env' -Raw
if ($envText -notmatch '(?m)^ADMIN_EMAIL=admin-recovery@local\.dev\s*$') {
    throw 'ADMIN_EMAIL in .env is not set to the bundled local admin email. Edit .env before starting.'
}
if ($envText -notmatch '(?m)^ADMIN_RESET_PASSWORD_ON_STARTUP=true\s*$') {
    Write-Warning 'Admin startup password recovery is disabled. Set ADMIN_RESET_PASSWORD_ON_STARTUP=true only for a private local development instance.'
}

Write-Host 'Building and starting Sentinel Fraud Risk Platform...' -ForegroundColor Cyan
docker compose up --build -d
if ($LASTEXITCODE -ne 0) { throw 'Docker Compose failed. Review the error above.' }

docker compose ps
if ($LASTEXITCODE -ne 0) { throw 'Could not list container status.' }

Write-Host ''
Write-Host 'Dashboard:  http://localhost:3001' -ForegroundColor Green
Write-Host 'API health: http://localhost:8081/health/ready' -ForegroundColor Green
Write-Host 'Local admin email: admin-recovery@local.dev' -ForegroundColor Green
Write-Host 'Enter the admin password from .env on the sign-in page. Password is not printed here.' -ForegroundColor Yellow
Write-Host 'This is a local-only demo. Disable ADMIN_RESET_PASSWORD_ON_STARTUP before any shared deployment.' -ForegroundColor Yellow
