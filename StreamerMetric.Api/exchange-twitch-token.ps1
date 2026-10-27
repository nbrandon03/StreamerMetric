
$ErrorActionPreference = "Stop"

$clientId = (dotnet user-secrets list | Select-String '^Twitch:ClientId\s*=').ToString().Split('=', 2)[1].Trim()
$clientSecret = (dotnet user-secrets list | Select-String '^Twitch:ClientSecret\s*=').ToString().Split('=', 2)[1].Trim()

if (-not $clientId -or -not $clientSecret) {
    throw "Twitch Client ID or Client Secret was not found in User Secrets."
}

$code = Read-Host "Paste the NEW Twitch authorization code"

$body = @{
    client_id = $clientId
    client_secret = $clientSecret
    code = $code
    grant_type = "authorization_code"
    redirect_uri = "http://localhost:5168/api/twitch/auth/callback"
}

$tokens = Invoke-RestMethod `
    -Method Post `
    -Uri "https://id.twitch.tv/oauth2/token" `
    -ContentType "application/x-www-form-urlencoded" `
    -Body $body

dotnet user-secrets set "Twitch:ChatAccessToken" $tokens.access_token
dotnet user-secrets set "Twitch:ChatRefreshToken" $tokens.refresh_token

Write-Host "Twitch tokens saved to .NET User Secrets."
Write-Host "Granted scopes:" ($tokens.scope -join ", ")
