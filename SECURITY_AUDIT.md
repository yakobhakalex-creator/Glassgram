# Pre-publication security review / Аудит перед публикацией

Updated: 26 November 2026. This is a record of limited local checks, not an independent security certification or a guarantee that the repository is free of secrets.

## Previous review — 25 November 2026

The previous audit recorded checks of the working tree, 32 reachable Git commits, filenames in history, six unreachable Git objects, remote URLs, known credential patterns and session-related paths. It reported no detected secrets or tracked Telegram session files. Gitleaks was not installed at that time. Those historical findings have not all been independently repeated in the follow-up review.

## Follow-up review — 26 November 2026

The following results were observed in the maintainer's local terminal output before publication:

| Check | Observed result |
| --- | --- |
| `git rev-list --count HEAD` | 53 commits |
| `gitleaks git --redact --log-opts="--all" .` | 53 commits scanned; no leaks found |
| `gitleaks dir --redact .` | 21 findings in the working directory |
| Finding paths | WebView2 `Secure Preferences` and `Local State` files under `release/` and `src/Glassgram/bin/` |
| `git ls-files -ci --exclude-standard` | No output |
| `git ls-files -- release src/Glassgram/bin` | No output |
| Exact-value search for the maintainer's Telegram API ID and API hash | No matching paths printed in searched text files and reachable revisions |
| Git author and committer metadata | Existing commits used a local agent identity, not the maintainer's personal email; local configuration was updated to use a GitHub noreply address for future commits |

The 21 WebView2 findings were not assessed as individually harmless. They are in generated browser-profile files outside the checked tracked paths. Such files must not be distributed, including in release archives. The actual values of the credentials are intentionally not recorded here.

## Session storage source review

`src/Glassgram/Telegram/SessionStorage.cs` uses a randomly generated 32-byte key, protected by Windows DPAPI (`CurrentUser`). The key and TDLib data paths are under the current user's local application data directory. No hard-coded secret was observed in the reviewed file. This does not verify every caller, the complete TDLib initialization flow, or protection against compromise of the Windows user account.

## Before publishing

- Complete or isolate unfinished changes and review the actual staged diff.
- Inspect `PROJECT_CONTEXT.md`, `CLAUDE.md`, other agent instructions and all documentation for personal data and internal paths.
- Confirm API-parameter storage and TDLib encryption-key use in the actual calling code.
- Repeat secret scanning after the final documentation and code changes, including all relevant Git refs. Exact-value text searches do not cover encoded values or all binary artifacts.
- Verify the distribution does not include sessions, message caches, `.env`, local settings, WebView2 profiles or personal data.
- Review author metadata and preserve third-party copyright and license notices.
- Build a fresh release from reviewed source and inspect the actual contents before uploading. Do not archive a previously used release directory.

Unreachable objects have not been rechecked in this follow-up, and the full source tree, final executable, license headers and binary distribution have not been independently audited. No publication is authorized by this report.
