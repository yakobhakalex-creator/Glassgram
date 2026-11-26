# Glassgram

**Your messenger. Your AI. Your freedom.**

[Русский](README.ru.md) · [Roadmap](ROADMAP.md) · [Contributing](CONTRIBUTING.md)

![License](https://img.shields.io/badge/license-GPL--3.0-blue)
![Platform](https://img.shields.io/badge/platform-Windows%20x64-0078D4)
![Status](https://img.shields.io/badge/status-early%20development-orange)

Glassgram is an independent Telegram client for Windows built with C#, WinUI 3 and TDLib. It combines a Liquid Glass-inspired interface, customizable navigation and local AI integration through Ollama.

The goal is to make a modern desktop messaging experience with optional local AI accessible without a mandatory paid AI subscription. Glassgram is an unofficial client and is not affiliated with Telegram or Apple.

## Current development milestone: V0.6.9

- Phone number or QR-code sign-in, verification codes and two-factor authentication.
- Private chats, groups, channels, chat folders, archives and Saved Messages.
- Real-time messaging, replies, editing, forwarding, deletion, reactions and pinned messages.
- Paginated history, restored scroll position, drafts and server-side chat search.
- Photos, videos, GIFs, documents, stickers (including WebM and TGS), animated emoji and voice messages.
- Group member management, group settings, invite links, join requests, forum topics and channel comments.
- Polls and quizzes, with voting and management where supported by Telegram permissions.
- Adjustable navigation, interface scaling from 75% to 200% and Windows notifications.
- Local integration with Ollama.

Glassgram is in early development. Not every operation has been tested end-to-end on live Telegram chats. Calls, Stories, secret chats, scheduled messages, data export and full administrative feature parity are not yet implemented.

## Getting started

Glassgram currently targets Windows x64. To sign in, obtain your own Telegram API ID and API hash at [my.telegram.org](https://my.telegram.org). Do not share your API hash, login codes, two-factor password or session files.

### Build from source

Requirements recorded in the project documentation: .NET 11 SDK with Windows Desktop support, a Windows development environment and restored NuGet dependencies. TDLib 1.8.67 is used by this milestone. Ollama is required only for the local AI integration.

From the repository root in PowerShell or CMD:

```powershell
dotnet build Glassgram.slnx -c Debug
```

To publish a local self-contained x64 build:

```powershell
dotnet publish src/Glassgram/Glassgram.csproj -c Release -r win-x64 --self-contained true -o release
```

The project documentation describes launching `release/Glassgram.exe`. Do not redistribute a release directory that has been used with a real account; prepare a clean package and include all required licenses and corresponding source code.

## Privacy and local data

TDLib session data is stored under `%LOCALAPPDATA%\Glassgram\Telegram`. The database encryption key is generated randomly and protected with Windows DPAPI for the current user. The project also documents DPAPI protection for API parameters and a local cache of up to 500 recent messages per chat.

Local storage protection does not make ordinary Telegram chats end-to-end encrypted. The Telegram client still communicates with Telegram services. The optional Ollama integration is intended to support local AI processing; do not assume all AI providers or configurations keep data on-device.

For the scope and limitations of the pre-publication review, see [SECURITY_AUDIT.md](SECURITY_AUDIT.md).

## Contributing

Bug reports, reproducible test cases, accessibility feedback and code contributions are welcome once the repository is published. See [CONTRIBUTING.md](CONTRIBUTING.md) and [ROADMAP.md](ROADMAP.md).

## License and acknowledgements

Glassgram source code is licensed under [GNU GPL v3.0](LICENSE). Third-party components retain their own licenses. Selected components were adapted from [Unigram](https://github.com/UnigramDev/Unigram), including emoji and sticker-related functionality, with attribution recorded in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Glassgram also relies on TDLib, Noto Color Emoji and other open-source and platform dependencies listed there.

Binary redistribution requires compliance with the applicable licenses, including provision of corresponding source under GPL-3.0 and preservation of copyright and license notices.
