# Contributing to Glassgram

Glassgram is an early-stage independent Telegram client. Contributions should be focused, reproducible and compatible with its GPL-3.0 license.

## Reporting bugs

Include the Glassgram version, Windows version, steps to reproduce, expected behavior and actual behavior. Remove chat contents, names, phone numbers, API credentials, tokens, login codes, local paths and session data from logs and screenshots.

Do not post real credentials or sensitive security details in a public issue. A private security reporting channel should be established by the maintainer before accepting vulnerability reports.

## Development workflow

1. Work on a dedicated Git branch and keep changes scoped to one issue.
2. Follow the existing C# and WinUI 3 architecture. Do not rename Glassgram as part of unrelated changes.
3. Build from the repository root with `dotnet build Glassgram.slnx -c Debug`.
4. Run the project's existing serialization regression tests and relevant feature tests. Record the commands, results and tests not performed.
5. For TDLib requests, check the required `@type` field on root and nested typed objects; do not substitute `type` for `@type`.
6. Update relevant documentation and submit a focused pull request with a clear description.

Use a test account or authorized test chats for operations that send, modify or delete messages. Do not test destructive or externally visible actions against other users without consent.

## Third-party code

When adapting GPL-compatible code, including Unigram components, preserve upstream copyright and license notices, record the source URL and commit, mark modifications and update `THIRD_PARTY_NOTICES.md`. Do not copy proprietary assets or code without permission. Contributions must be distributable under the project's applicable license terms.

## Local data and release files

Never commit Telegram sessions, databases, API hashes, bot tokens, private configuration, `.env` files, WebView2 profiles, or build output. Use placeholder values in example configurations. Review the staged diff and run secret scanning before committing or publishing.
