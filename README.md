# Aura v1 — ИИ-помощник для Windows

Трей-приложение: **Win+Shift+A** → оверлей (mica, тёмная тема) у края экрана с 3 карточками:
1. **Что на экране** — заголовок активного окна + процесс (без скриншота).
2. **Следующий шаг** — подсказка по типу приложения (браузер / Office / проводник / код); клик = заметка.
3. **Готовое действие** — Свернуть · Заметка · Таймер 5/10/25 мин (уведомление из трея).

Индикатор «Смотрю · <app>» виден всё время, пока открыт оверлей; **Пауза** — в индикаторе и в трее (при паузе контекст не читается вообще).

## Сборка (PC Bot, DESKTOP)
Нужен .NET 8 SDK.
```powershell
cd src\Aura
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
New-Item -ItemType Directory -Force $env:USERPROFILE\Apps\Aura | Out-Null
Copy-Item publish\Aura.exe $env:USERPROFILE\Apps\Aura\ -Force
& $env:USERPROFILE\Apps\Aura\Aura.exe
```
Автозагрузка (по желанию): ярлык на `Aura.exe` в `shell:startup`.

## Данные
- Настройки: `%AppData%\Aura\settings.json` — `Paused`, `ScreenCaptureOptIn` (по умолчанию `false`), `ExcludedApps`, `DefaultTimerMinutes`.
- Заметки: `%UserProfile%\Documents\Aura\notes.txt`.

## Приватность (Hack Formal)
- Хоткей через `RegisterHotKey` — **никаких** `SetWindowsHookEx`, кейлоггера, хуков мыши.
- Контекст читается **один раз по нажатию** хоткея: `GetForegroundWindow`, `GetWindowText`, `GetWindowThreadProcessId`.
- Буфер обмена не читается. Скриншот/OCR — только opt-in; в v1 это заглушка, ничего не снимается.
- Сеть не используется. Приложения из `ExcludedApps` (менеджеры паролей) — заголовок скрыт.

Дизайн-референс: `src/Aura/Assets/overlay-v1.design.html` (Design Formal v1).
