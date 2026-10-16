# Публикация на GitHub

Проект подготовлен в обычной папке. Внешний репозиторий ещё не создан. Ниже два способа: через установленный Git либо целиком через GitHub CLI.

## Через установленный Git

1. Откройте [создание репозитория](https://github.com/new).
2. Назовите его `AngleReader`, выберите Public или Private.
3. Оставьте выключенными добавление README, .gitignore и лицензии: локальные файлы уже подготовлены.
4. Нажмите **Create repository**.

Откройте терминал в папке проекта. Если используете распакованный архив, укажите фактический путь.

PowerShell:

```powershell
Set-Location "C:\Projects\AngleReader"
```

CMD:

```bat
cd /d "C:\Projects\AngleReader"
```

Далее команды одинаковые для PowerShell и CMD. **Замените `YOUR_LOGIN` своим именем пользователя GitHub**. Если выбрали другое имя репозитория, замените и `AngleReader` в URL.

```console
git init -b main
git add .
git status --short
git commit -m "Initial release: AngleReader 0.5.0"
git remote add origin https://github.com/YOUR_LOGIN/AngleReader.git
git push -u origin main
```

Команды рассчитаны на новый локальный и пустой удалённый репозиторий. При первом push завершите предложенный вход в GitHub. Учётные данные не добавляются в исходники.

Если `git commit` просит имя и email, задайте их **для этого репозитория**, затем повторите commit и оставшиеся команды:

```console
git config user.name "YOUR_NAME"
git config user.email "YOUR_EMAIL"
git commit -m "Initial release: AngleReader 0.5.0"
```

Замените `YOUR_NAME` и `YOUR_EMAIL` собственными значениями. Можно использовать noreply-адрес из настроек email вашего аккаунта GitHub.

Если origin уже существует, проверьте его:

```console
git remote -v
```

Для исправления адреса:

```console
git remote set-url origin https://github.com/YOUR_LOGIN/AngleReader.git
```

## Целиком через GitHub CLI

Этот способ создаёт репозиторий из терминала. Используйте его вместо предыдущего способа создания удалённого репозитория.

Если команда `gh` отсутствует, установите [GitHub CLI](https://cli.github.com/), затем заново откройте терминал. Перейдите в папку проекта, как показано выше.

```console
gh auth login --web --git-protocol https
git init -b main
git add .
git commit -m "Initial release: AngleReader 0.5.0"
gh repo create AngleReader --private --source=. --remote=origin --push
```

В примере создаётся приватный репозиторий. Для публичного замените `--private` на `--public`.

## После загрузки

Откройте вкладку **Actions**. Workflow `Windows build and tests` должен завершиться успешно. В его **Artifacts** будут переносимая сборка и отчёт проверок.

Чтобы разместить EXE для скачивания через Releases, сначала соберите архив:

```console
.\package.cmd
```

С установленным и авторизованным GitHub CLI:

```console
gh release create v0.5.0 "dist/AngleReader-v0.5.0-win-x64.zip" "dist/AngleReader-v0.5.0-win-x64.zip.sha256" --target main --title "AngleReader 0.5.0" --notes-file CHANGELOG.md
```

Либо создайте Release через веб-интерфейс GitHub и прикрепите эти два файла вручную.

## Последующие изменения

После изменения кода:

```console
.\test.cmd
git add .
git commit -m "Describe the change"
git push
```

Не меняйте версию без соответствующего обновления `VERSION`, `src/AssemblyInfo.cs` и `CHANGELOG.md`.

## Справка

- [GitHub: добавление локального проекта](https://docs.github.com/en/migrations/importing-source-code/using-the-command-line-to-import-source-code/adding-locally-hosted-code-to-github)
- [GitHub CLI: создание репозитория](https://cli.github.com/manual/gh_repo_create)
- [GitHub CLI: создание Release](https://cli.github.com/manual/gh_release_create)

