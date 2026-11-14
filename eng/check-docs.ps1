# Fails when a user-facing document carries development history: see docs/writing-user-docs.md.
param([string[]] $Path = @('README.md', 'SECURITY.md'))
$ErrorActionPreference = 'Stop'

# Upgrade steps and compatibility statements name versions and what an older version left behind.
$history = '\bno longer\b|\bpreviously\b|\bused to\b|\bany ?more\b|\b(is|are) now\b|\bnow (sets|says|say|writes|creates|uses|runs|shows)\b|; before, |\bas in 0\.\d|\bsince 0\.\d'
$rules = @(
    @{ Name = 'issue or pull request number outside a link'; Pattern = '(?i)(?<!\[)\b(issue|pull request|PR) #?\d+\b(?!\]\()' },
    @{ Name = 'ADR number outside a link'; Pattern = '(?<!\[)\bADR \d{4}\b(?![^\[\]]*\]\()' },
    @{ Name = 'ADR number in a heading'; Pattern = '^#+ .*\bADR \d{4}\b' },
    @{ Name = 'change-history wording'; Pattern = "(?i)$history" },
    @{ Name = 'source file name'; Pattern = '(?<![\w/\\.-])(?!harness\.cs\b|lib\.cs\b)[\w.-]+\.(cs|gd|cpp|h)\b' },
    @{ Name = 'drafting leftover'; Pattern = '\b(TODO|PENDING|TBD)\b|\d{4}-\d{2}-DD' },
    @{ Name = 'about how Cluewren is tested'; Pattern = '(?i)\bcensus\b|\btest suite\b' }
)

$found = 0
foreach ($file in $Path) {
    $number = 0
    $section = ''
    $fenced = $false
    foreach ($line in Get-Content -LiteralPath $file) {
        $number++
        if ($line -match '^\s*```') { $fenced = -not $fenced; continue }
        if ($fenced) { continue }
        if ($line -match '^#+ (.*)') { $section = $Matches[1] }
        foreach ($rule in $rules) {
            if ($rule.Name -eq 'change-history wording' -and $section -match '(?i)upgrad') { continue }
            # Quoted program output is the program's wording, not the document's.
            $text = if ($rule.Name -eq 'change-history wording') { $line -replace '"[^"]*"', '""' } else { $line }
            if ($text -match $rule.Pattern) {
                $found++
                Write-Output ('{0}:{1}: {2}: {3}' -f $file, $number, $rule.Name, $Matches[0])
            }
        }
    }
}
if ($found -gt 0) {
    Write-Output "$found finding(s). The rules are in docs/writing-user-docs.md."
    exit 1
}
Write-Output 'User-facing documents: no development history found.'
