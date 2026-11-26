# Glassgram — CLAUDE.md

## Архитектура и модули

| Путь | Назначение |
|------|------------|
| `src/Glassgram/` | WinUI 3 приложение (.NET 11, x64) |
| `Telegram/TdClient.cs` | P/Invoke-обёртка TDLib: отправка, приём, `@extra` |
| `Telegram/TdRequestSerializer.cs` | Сериализация JSON с `@type` вместо `type` |
| `Telegram/TelegramModels.cs` | `ChatRow`, `MessageRow` — UI-модели, парсинг контента/медиа/реакций |
| `Telegram/SessionStorage.cs` | DPAPI-ключ шифрования БД TDLib |
| `Telegram/VoiceNotePipeline.cs` | PCM → Ogg/Opus (Concentus), валидация декодером |
| `Telegram/MessageHistoryCache.cs` | DPAPI-кэш последних 500 сообщений/чат |
| `Telegram/ReactionModels.cs` | `ReactionRow`, реакции в `MessageRow` |
| `Telegram/UnigramEmojiCatalog.cs` | Каталог эмодзи (адаптирован из Unigram GPL-3.0) |
| `MainWindow.xaml.cs` + partial | Весь UI: чаты, сообщения, медиа, голосовые, стикеры, профили, ИИ, тема |
| `Controls/GlassPanel.cs` | Кастомный ContentControl с `GlassOpacity` |
| `tests/Glassgram.Serialization.Tests/` | Регрессионные тесты сериализации TDLib, Ogg/Opus, реакций |

Зависимости: `tdlib.native.win-x64` 1.8.67, WinUI 2.7, NAudio, Concentus, QRCoder, Lottie, WebView2.

---

## Сборка, тесты, публикация

```powershell
# Debug
dotnet build C:\Glassgram\Glassgram.slnx -c Debug

# Release (self-contained, x64)
dotnet publish C:\Glassgram\src\Glassgram\Glassgram.csproj -c Release -r win-x64 --self-contained true -o C:\Glassgram\release

# Тесты
dotnet test C:\Glassgram\tests\Glassgram.Serialization.Tests\Glassgram.Serialization.Tests.csproj
```

Готовый `.exe`: `C:\Glassgram\release\Glassgram.exe` (без консоли).

После каждого успешно проверенного обновления обязательно выполнить Release self-contained publish в `C:\Glassgram\release`, затем проверить запуск через `Glassgram.exe` на рабочем столе. Desktop-файл является постоянным лаунчером актуальной Release-сборки; основной EXE нельзя копировать отдельно от зависимостей.

---

## TDLib интеграция — критические правила

1. **`@type` обязателен** в **каждом** типизированном JSON-объекте (корне и вложенные). C# свойство `type` сериализуется как `type` — `TdRequestSerializer` рекурсивно заменяет на `@type`.
2. Запрос без `@type` на корне — ошибка сериализации.
3. Объект с обоими `type` и `@type` — ошибка.
4. Ответы TDLib приходят через `Update` event; запросы сопоставляются через `@extra` (Int64).
5. Сессия хранится в `%LOCALAPPDATA%\Glassgram\Telegram\`; ключ БД — DPAPI.

---

## Git-рабочий процесс

- **Коммит только после успешной сборки** (`dotnet build`) и прохождения тестов (`dotnet test`).
- `.gitignore` исключает: `bin/`, `obj/`, `.vs/`, `*.session`, `*.sqlite*`, `.env*`, `secrets.json`, `Telegram/database/`, `MessageCache/`, `settings.json`, `release/`.
- Не коммитить: TDLib-сессии, базы, кэш, настройки, API ID/hash, номера телефонов, сборочные артефакты.

---

## Лицензии

- **Glassgram** — GPL-3.0-only. При распространении бинарей **обязан** предоставить исходники.
- Зависимости: MIT/BSD/Apache/BSL-1.0 — совместимы с GPL-3.0. Сохранять copyright/notice в дистрибутиве.
- Адаптирован код Unigram (GPL-3.0) из commit `9354bcb753be3b16086bd3886b8adec062ccdd91` — заголовки сохранены, изменения документированы в `THIRD_PARTY_NOTICES.md`.
- Self-contained publish копирует Microsoft-рантайм (WinUI, WebView2, App SDK) — их лицензии не перелицензируются под GPL; включать их `license.txt`/`NOTICE.txt`.

---

## Жёсткие запреты

- ❌ **Не переименовывать** проект/бинарник/пространство имён из `Glassgram`.
- ❌ **Не читать** секреты, токены, API hash, TDLib-сессии, базы, кэш, `.env`, пользовательские настройки.
- ❌ Не сканировать `bin/`, `obj/`, `release/`, `.git/`, кэши при обычном поиске — используй точечные `Glob`/`Grep` по `src/`, `tests/`.

---

## Экономия токенов

- Читай **только необходимые файлы** по известным путям.
- Не перечитывай проект целиком — используй контекст уже изученного.
- Для поиска: `Glob("src/**/*.cs")`, `Grep(pattern, "src/")` — исключай `bin/obj/release/.git`.

---

## Ответы

Кратко, по делу, без лишних объяснений. Код не менять без явного запроса.

---

## Процесс работы

- Перед изменениями проверить `git status`. Не перезаписывать чужие изменения.
- Не проводить масштабный рефакторинг без согласования.
- Не менять существующий функционал и дизайн вне поставленной задачи.
- После задачи: кратко — изменённые файлы, результаты тестов, хеш коммита (если создан).
