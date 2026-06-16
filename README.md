# MAX Bot для приёмной комиссии колледжа

Консольный бот на C# для мессенджера MAX.  
Бот показывает даты дней открытых дверей, список специальностей с подробным описанием, корпуса колледжа, сроки обучения и ответы на часто задаваемые вопросы абитуриентов.

## Возможности

- Главное меню с разделами:
  - Даты Дней открытых дверей
  - Специальности (с деталями по каждой)
  - Корпуса для ДОД
  - Срок обучения
  - Часто задаваемые вопросы
  - Иностранные граждане
  - Сотрудничество с ВУЗами
  - Перевод из другого учебного заведения
  - Переход на сайт колледжа
- Привязка `user_id` → `chat_id` в PostgreSQL, чтобы бот мог отвечать в нужный диалог.
- Загрузка данных (дни открытых дверей, специальности, FAQ и т.д.) из базы PostgreSQL.
- Автоматический перезапуск long‑polling клиента, если нет апдейтов.

## Технологии

- .NET (C#) — консольное приложение
- [MAX.Bot](https://max-messenger.ru/) — клиент для API мессенджера MAX
- PostgreSQL + Npgsql

## Подготовка и запуск

1. **Клонировать репозиторий**

```bash
git clone https://github.com/USER/REPO.git
cd REPO
```

2. **Настроить строку подключения к БД**

В файле `Program.cs`:

```csharp
private static readonly string ConnectionString =
    "Host=89.110.53.87;Port=50000;Database=max_bot;Username=postgres;Password";
```

Замени на свои реальные данные (host, port, db, user, password).

3. **Указать токен бота**

В проекте используется класс `bot_token`:

```csharp
var token = bot_token.token;
```

Создай этот класс (если его нет в репозитории) и пропиши туда токен, выданный в кабинете MAX.

4. **Проверить схему БД**

Бот ожидает наличие таблиц (имена можно скорректировать под свою БД):

- `user_chats (user_id, chat_id)`
- `open_door_time (id, even_date)`
- `specialties_list (id, cod, title)`
- `filling_in_data_for_specializations (id, specialty_id, content)`
- `basic_education (id, education_info)`
- `specialty_basic_education (specialty_id, basic_education_id)`
- `college_branches (id, branch_name, adress, metro_station)`
- `admission_faq (id, admission_id, question, display_order)`
- `information_stat (id, specialty_id, title, content)`
- `transfer_page_content (id, top_content, middle_text, bottom_content)`

5. **Собрать и запустить**

```bash
dotnet build
dotnet run
```

После запуска в консоли появится сообщение:

```text
Бот запущен. Для остановки закройте консоль.
```

Теперь можно написать боту в MAX и нажать «Начать».

## Как это работает

- При первом старте диалога (`BotStartedUpdate`):
  - сохраняется `user_id` и `chat_id` в таблицу `user_chats`;
  - пользователю отправляется главное меню.
- При входящих сообщениях (`MessageCreatedUpdate`):
  - текст команды определяется по кнопкам/строке;
  - бот подгружает данные из PostgreSQL и отправляет ответ с инлайн‑клавиатурой «Вернуться в меню».
- Клиент long‑polling проверяет активность и перезапускается, если долго нет апдейтов.

## Возможные ошибки

- `HTTP Forbidden: {"code":"chat.denied","message":"Key: error.dialog.suspended, ..."}`
  - Диалог с пользователем на стороне MAX приостановлен или бот заблокирован.
  - Решение: пользователь должен заново запустить бота в MAX; при необходимости обновить `chat_id` в таблице `user_chats` или удалить старую запись.
