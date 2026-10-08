$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$skillsRoot = Join-Path $projectRoot 'app/skills'
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'skills-catalog.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($skill in $catalog) {
    if ($skill.name -notmatch '^[a-z][a-z0-9-]{0,63}$') { throw 'Nombre de skill inválido' }
    $folder = Join-Path $skillsRoot $skill.name
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    $text = "---\nname: $($skill.name)\ndescription: $($skill.description | ConvertTo-Json -Compress)\nbackend: $($skill.backend)\nemoji: $($skill.emoji | ConvertTo-Json -Compress)\ntriggers: $($skill.triggers | ConvertTo-Json -Compress)\n---\n\n$($skill.instructions)\n"
    $text = $text.Replace('\n', [Environment]::NewLine)
    [IO.File]::WriteAllText((Join-Path $folder 'SKILL.md'), $text, [Text.UTF8Encoding]::new($false))
}
Write-Output "Skills adaptadas generadas: $($catalog.Count); las seis skills base (code, desktop, browser, models, images y scenes) se conservan."

