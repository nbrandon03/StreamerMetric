
$ErrorActionPreference = "Stop"

$clientId = (dotnet user-secrets list | Select-String '^Twitch:ClientId\s*=').ToString().Split('=', 2)[1].Trim()
$clientSecret = (dotnet user-secrets list | Select-String '^Twitch:ClientSecret\s*=').ToString().Split('=', 2)[1].Trim()
$refreshToken = (dotnet user-secrets list | Select-String '^Twitch:ChatRefreshToken\s*=').ToString().Split('=', 2)[1].Trim()

if (-not $clientId -or -not $clientSecret -or -not $refreshToken) {
    throw "Twitch Client ID, Client Secret, or Refresh Token was not found in User Secrets."
}

$body = @{
    client_id = $clientId
    client_secret = $clientSecret
    grant_type = "refresh_token"
    refresh_token = $refreshToken
}

$tokens = Invoke-RestMethod `
    -Method Post `
    -Uri "https://id.twitch.tv/oauth2/token" `
    -ContentType "application/x-www-form-urlencoded" `
    -Body $body

dotnet user-secrets set "Twitch:ChatAccessToken" $tokens.access_token
dotnet user-secrets set "Twitch:ChatRefreshToken" $tokens.refresh_token

Write-Host "Twitch tokens refreshed and saved to .NET User Secrets."
Write-Host "Granted scopes:" ($tokens.scope -join ", ")
