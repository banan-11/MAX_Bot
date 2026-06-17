using MAX.Bot;
using MAX.Bot.Interfaces;
using MAX.Bot.Interfaces.Models;
using MAX.Bot.Interfaces.Models.Request;
using MAX.Bot.Interfaces.Models.Request.Message;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment;
using MAX.Bot.Interfaces.Models.Request.Message.Attachment.Payloads;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Create_max_bot
{
    internal class Program
    {
        private static readonly string ConnectionString = Db_Config.ConnectionString;

        private static DateTime _lastUpdateTime = DateTime.UtcNow;

        static async Task Main(string[] args)
        {
            var token = bot_token.token;

            while (true)
            {
                try
                {
                    Console.WriteLine("Бот запущен. Для остановки закройте консоль.");
                    var client = new MaxBotClient(token);

                    _lastUpdateTime = DateTime.UtcNow;

                    var _ = client.PollUpdatesWithCallback(
                        async (update, api) =>
                        {
                            _lastUpdateTime = DateTime.UtcNow;

                            Console.WriteLine($"[UPDATE] type={update.UpdateType}");

                            if (update is BotStartedUpdate started)
                            {
                                long chatId = started.ChatId;
                                long userId = started.User?.Id ?? 0;

                                Console.WriteLine($"[BOT_STARTED] user={userId}, chat={chatId}");

                                if (userId != 0)
                                    await SaveUserChatAsync(userId, chatId);

                                await SendMainMenu(chatId, api);
                            }
                            else if (update is MessageCreatedUpdate messageCreated)
                            {
                                await HandleMessage(messageCreated, api);
                            }
                        },
                        limit: 100,
                        timeout: 90,
                        types: new List<string>
                        {
                            UpdateTypes.BotStarted,
                            UpdateTypes.MessageCreated
                        });

                    while (true)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10));

                        var idle = DateTime.UtcNow - _lastUpdateTime;

                        if (idle > TimeSpan.FromSeconds(25))
                        {
                            Console.WriteLine($"Нет апдейтов {idle.TotalSeconds:F0} сек. Перезапуск клиента...");
                            break;
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка в Main: {ex}");
                    Console.WriteLine("Перезапуск через 1 секунду");
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
            }
        }

        // user_id -> chat_id

        private static async Task SaveUserChatAsync(long userId, long chatId)
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
                INSERT INTO user_chats (user_id, chat_id)
                VALUES (@uid, @cid)
                ON CONFLICT (user_id) DO UPDATE
                SET chat_id = EXCLUDED.chat_id;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("uid", userId);
            cmd.Parameters.AddWithValue("cid", chatId);
            await cmd.ExecuteNonQueryAsync();
        }

        private static async Task<long> GetChatIdByUserAsync(long userId)
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT chat_id FROM user_chats WHERE user_id = @uid LIMIT 1";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("uid", userId);

            var result = await cmd.ExecuteScalarAsync();
            return result is long cid ? cid : 0;
        }

        // обработка сообщений 

        private static async Task HandleMessage(MessageCreatedUpdate update, IMaxBotClient api)
        {
            var text = update.Message?.Body?.Text ?? string.Empty;
            long userId = update.Message?.Sender?.Id ?? 0;

            Console.WriteLine($"[MESSAGE] from user={userId}, text='{text}'");

            if (string.IsNullOrWhiteSpace(text) || userId == 0)
                return;

            long chatId = await GetChatIdByUserAsync(userId);

            if (chatId == 0)
            {
                Console.WriteLine($"ChatId for user {userId} not found in DB");
                return;
            }

            if (text == "/start" || text.Equals("Начать", StringComparison.OrdinalIgnoreCase))
            {
                await SendMainMenu(chatId, api);
                return;
            }

            // Кнопка назад
            if (text.Equals("Вернуться в меню", StringComparison.OrdinalIgnoreCase) ||
                text.Equals("Назад", StringComparison.OrdinalIgnoreCase))
            {
                await SendMainMenu(chatId, api);
                return;
            }

            // 1. Попытка воспринять текст как название специальности
            var specialtyIdByTitle = await TryGetSpecialtyIdByTitleAsync(text);
            if (specialtyIdByTitle > 0)
            {
                await SendSpecialtyDetails(chatId, api, specialtyIdByTitle);
                return;
            }

            // 2. Старые форматы "Специальность N" и "Спец N" оставим как запасной путь
            if (text.StartsWith("Специальность ", StringComparison.OrdinalIgnoreCase))
            {
                var numPart = text.Substring("Специальность ".Length).Trim();
                if (int.TryParse(numPart, out var specIdFromButton))
                {
                    await SendSpecialtyDetails(chatId, api, specIdFromButton);
                    return;
                }
            }

            if (text.StartsWith("Спец ", StringComparison.OrdinalIgnoreCase))
            {
                var numPart = text.Substring("Спец ".Length).Trim();
                if (int.TryParse(numPart, out var specId))
                {
                    await SendSpecialtyDetails(chatId, api, specId);
                    return;
                }
            }

            // 3. Остальные команды по точному тексту

            switch (text)
            {
                case "Даты дней открытых дверей":
                    await SendOpenDays(chatId, api);
                    break;

                case "Специальности":
                    await SendSpecialties(chatId, api);
                    break;

                case "Специальности по площадкам":
                    await SendSpecialtiesByBranch(chatId, api);
                    break;

                case "Корпуса колледжа":
                case "Корпуса для ДОД":
                    await SendBuildings(chatId, api);
                    break;

                case "Срок обучения":
                    await SendDuration(chatId, api);
                    break;

                case "Часто задаваемые вопросы":
                    await SendFaqMenu(chatId, api);
                    break;

                case "Иностранные граждане":
                    await SendInfoBlock(chatId, api, infoType: "foreign");
                    break;

                case "Сотрудничество с ВУЗами":
                    await SendInfoBlock(chatId, api, infoType: "universities");
                    break;

                case "Перевод из другого учебного заведения":
                    await SendTransferInfo(chatId, api);
                    break;

                case "посетить сайт кгтс":
                    await SendKgtcSiteLink(chatId, api);
                    break;

                case "Контакты":
                    await SendContacts(chatId, api);
                    break;

                default:
                    Console.WriteLine("Я ничего не понял :((((");
                    break;
            }
        }

        // Поиск id специальности по полному названию (точное совпадение)
        private static async Task<int> TryGetSpecialtyIdByTitleAsync(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return 0;

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT id
                FROM specialties_list
                WHERE title = @t
                LIMIT 1;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("t", title.Trim());

            var result = await cmd.ExecuteScalarAsync();
            return result is int i ? i : 0;
        }

        // главное меню 

        private static async Task SendMainMenu(long chatId, IMaxBotClient api)
        {
            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Даты дней открытых дверей", "open_days")),
                Row(CallbackButton("Корпуса колледжа", "buildings")),
                Row(CallbackButton("Специальности", "specialties")),
                Row(CallbackButton("Специальности по площадкам", "specialties_by_branch")),
                Row(CallbackButton("Срок обучения", "duration")),
                Row(CallbackButton("Часто задаваемые вопросы", "faq")),
                Row(CallbackButton("Иностранные граждане", "foreign")),
                Row(CallbackButton("Сотрудничество с ВУЗами", "universities")),
                Row(CallbackButton("Перевод из другого учебного заведения", "transfer")),
                Row(CallbackButton("посетить сайт кгтс", "kgtc_site")),
                Row(CallbackButton("Контакты", "contacts"))
            };

            var keyboard = BuildInlineKeyboard(rows);

            var req = new SendMessageRequest
            {
                ChatId = chatId,
                Text = "Выберите интересующий раздел:",
                Format = MessageFormat.Markdown,
                Attachments = new List<Attachment> { keyboard }
            };

            await api.SendMessageAsync(req);
        }

        // дни открытых дверей 

        private static async Task SendOpenDays(long chatId, IMaxBotClient api)
        {
            var items = await LoadOpenDaysAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Ближайшие дни открытых дверей:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine($"{d.Id}. {d.Date:dd.MM.yyyy}");

            sb.AppendLine();
            sb.AppendLine("Дни открытых дверей проходят единовременно во всех учебных корпусах Колледжа.");

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        private static async Task<List<(int Id, DateTime Date)>> LoadOpenDaysAsync()
        {
            var result = new List<(int, DateTime)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT id, even_date FROM open_door_time ORDER BY even_date";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add((reader.GetInt32(0), reader.GetDateTime(1)));
            }
            return result;
        }

        // список специальностей 

        private static async Task SendSpecialties(long chatId, IMaxBotClient api)
        {
            var specialties = await LoadSpecialtiesAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Список специальностей:");
            sb.AppendLine();

            foreach (var s in specialties)
            {
                sb.AppendLine($"{s.Id}. {s.Cod} — {s.Title}");
            }

            var rows = new List<List<MessageButton>>();

            foreach (var s in specialties)
            {
                var btnText = s.Title;
                rows.Add(new List<MessageButton> { CallbackButton(btnText, "spec") });
            }

            rows.Add(new List<MessageButton>
            {
                CallbackButton("Вернуться в меню", "back_to_menu")
            });

            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        private static async Task<List<(int Id, string Cod, string Title)>> LoadSpecialtiesAsync()
        {
            var result = new List<(int, string, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql =
                "SELECT id, cod, title " +
                "FROM specialties_list " +
                "ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
            return result;
        }

        // спецухи по branch_id для корпуса

        private static async Task<List<(int Id, string Cod, string Title)>> LoadSpecialtiesByBranchAsync(int branchId)
        {
            var result = new List<(int, string, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql =
                "SELECT id, cod, title " +
                "FROM specialties_list " +
                "WHERE branch_id = @bid " +
                "ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("bid", branchId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
            return result;
        }

        // информация о специальности + срок 

        private static async Task SendSpecialtyDetails(long chatId, IMaxBotClient api, int specialtyId)
        {
            var details = await LoadSpecialtyDetailsAsync(specialtyId);
            var duration = await LoadDurationForSpecialtyAsync(specialtyId);

            if (details.Count == 0 && string.IsNullOrWhiteSpace(duration))
            {
                var rowsEmpty = new List<List<MessageButton>>
                {
                    Row(CallbackButton("Вернуться в меню", "back_to_menu"))
                };
                var kbEmpty = BuildInlineKeyboard(rowsEmpty);

                await api.SendMessageAsync(new SendMessageRequest
                {
                    ChatId = chatId,
                    Text = "Информация по этой специальности пока не заполнена.",
                    Attachments = new List<Attachment> { kbEmpty }
                });
                return;
            }

            string title = await LoadSpecialtyTitleAsync(specialtyId);

            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine(title);
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(duration))
            {
                sb.AppendLine("**Срок обучения:**");
                sb.AppendLine(duration);
                sb.AppendLine();
            }

            var headers = new[]
            {
                "**Квалификация:**",
                "**Краткое описание специальности:**",
                "**Область профессиональной деятельности:**",
                "**Где работает:**",
                "**Средства труда:**",
                "**Основные виды деятельности:**",
                "**Какими качествами должен обладать:**",
                "**Должности в организациях:**"
            };

            for (int i = 0; i < details.Count; i++)
            {
                if (i < headers.Length)
                    sb.AppendLine(headers[i]);

                var line = details[i]
                    .Replace("\r\n", "\n")
                    .Replace("\r", "\n")
                    .Trim();

                var parts = line
                    .Split(new[] { '•' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (parts.Count > 1)
                {
                    foreach (var part in parts)
                        sb.AppendLine("• " + part);
                }
                else
                {
                    sb.AppendLine(line);
                }

                sb.AppendLine();
            }

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Format = MessageFormat.Markdown,
                Attachments = new List<Attachment> { keyboard }
            });
        }

        private static async Task<string> LoadSpecialtyTitleAsync(int specialtyId)
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT title FROM specialties_list WHERE id = @id LIMIT 1";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", specialtyId);

            var result = await cmd.ExecuteScalarAsync();
            return result as string ?? string.Empty;
        }

        private static async Task<List<string>> LoadSpecialtyDetailsAsync(int specialtyId)
        {
            var result = new List<string>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT content FROM filling_in_data_for_specializations WHERE specialty_id = @id ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", specialtyId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }

        private static async Task<string> LoadDurationForSpecialtyAsync(int specialtyId)
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT be.education_info
                FROM specialty_basic_education sbe
                JOIN basic_education be ON be.id = sbe.basic_education_id
                WHERE sbe.specialty_id = @specId
                ORDER BY be.id;
            ";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("specId", specialtyId);

            var list = new List<string>();

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                list.Add(reader.GetString(0));
            }

            if (list.Count == 0)
                return "Информация о сроке обучения не найдена.";

            return "• " + string.Join("\n• ", list);
        }

        // корпуса 

        private static async Task SendBuildings(long chatId, IMaxBotClient api)
        {
            var items = await LoadBranchesFullAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Учебные корпуса колледжа:");
            sb.AppendLine();

            foreach (var b in items)
            {
                sb.AppendLine(b.BranchName);
                sb.AppendLine(b.Address);
                sb.AppendLine("Метро: " + b.Metro);
                sb.AppendLine();
            }

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // корпуса sql 

        private static async Task<List<(int Id, string BranchName, string Address, string Metro)>> LoadBranchesFullAsync()
        {
            var result = new List<(int, string, string, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT id, branch_name, adress, metro_station FROM college_branches ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3)
                ));
            }
            return result;
        }

        // Специальности по площадкам (корпусам)

        private static async Task SendSpecialtiesByBranch(long chatId, IMaxBotClient api)
        {
            var intro = new StringBuilder();
            intro.AppendLine("1 год после 9 все обучаются на Луначарского 66,");
            intro.AppendLine("далее распределяются по направлениям подготовки.");
            intro.AppendLine();
            intro.AppendLine("Ниже — корпуса и доступные в них специальныеости:");

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = intro.ToString()
            });

            var branches = await LoadBranchesFullAsync();

            foreach (var b in branches)
            {
                var specialties = await LoadSpecialtiesByBranchAsync(b.Id);

                var sb = new StringBuilder();
                sb.AppendLine(b.BranchName);
                sb.AppendLine(b.Address);
                sb.AppendLine("Метро: " + b.Metro);
                sb.AppendLine();

                if (specialties.Count == 0)
                {
                    sb.AppendLine("Информация о специальностях для этого корпуса пока не заполнена.");
                    await api.SendMessageAsync(new SendMessageRequest
                    {
                        ChatId = chatId,
                        Text = sb.ToString()
                    });
                    continue;
                }

                sb.AppendLine("Доступные специальныеости:");

                var rows = new List<List<MessageButton>>();

                foreach (var s in specialties)
                {
                    var btnText = s.Title;
                    rows.Add(new List<MessageButton> { CallbackButton(btnText, "spec") });
                }

                var keyboard = BuildInlineKeyboard(rows);

                await api.SendMessageAsync(new SendMessageRequest
                {
                    ChatId = chatId,
                    Text = sb.ToString(),
                    Attachments = new List<Attachment> { keyboard }
                });
            }
        }

        // общий список сроков 

        private static async Task SendDuration(long chatId, IMaxBotClient api)
        {
            var items = await LoadBasicEducationAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Сроки обучения:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine("• " + d);

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // общий список сроков sql 

        private static async Task<List<string>> LoadBasicEducationAsync()
        {
            var result = new List<string>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT education_info FROM basic_education ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add(reader.GetString(0));
            }
            return result;
        }

        // FAQ 

        private static async Task SendFaqMenu(long chatId, IMaxBotClient api)
        {
            var faqItems = await LoadFaqTitlesAsync(admissionId: 1);

            var sb = new StringBuilder();
            sb.AppendLine("Часто задаваемые вопросы:");
            sb.AppendLine();

            foreach (var item in faqItems)
            {
                sb.AppendLine($"{item.Id}. {item.Question}");
                sb.AppendLine();
            }

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // FAQ sql 

        private static async Task<List<(int Id, string Question)>> LoadFaqTitlesAsync(int admissionId)
        {
            var result = new List<(int, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT id, question
                FROM admission_faq
                WHERE admission_id = @adm AND display_order > 0
                ORDER BY display_order
            ";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("adm", admissionId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var q = reader.IsDBNull(1) ? "" : reader.GetString(1);
                result.Add((id, q));
            }
            return result;
        }

        // иностранцы / ВУЗы 

        private static async Task SendInfoBlock(long chatId, IMaxBotClient api, string infoType)
        {
            var content = await LoadInformationStatAsync(infoType);

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = content,
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // иностранцы / ВУЗы sql 

        private static async Task<string> LoadInformationStatAsync(string type)
        {
            var filter = type switch
            {
                "foreign" => "Прием иностранных граждан",
                "universities" => "Сотрудничество с ВУЗами",
                _ => ""
            };

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT content
                FROM information_stat
                WHERE specialty_id = 1 AND title LIKE @title
                ORDER BY id LIMIT 1
            ";
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("title", filter + "%");

            var result = await cmd.ExecuteScalarAsync();
            return result as string ?? "Информация временно недоступна.";
        }

        // перевод из другого учебного заведения 

        private static async Task SendTransferInfo(long chatId, IMaxBotClient api)
        {
            var (top, middle, bottom) = await LoadTransferPageAsync();

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(top))
            {
                sb.AppendLine(top);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(middle))
            {
                sb.AppendLine(middle);
                sb.AppendLine();
            }
            if (!string.IsNullOrWhiteSpace(bottom))
                sb.AppendLine(bottom);

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // перевод из другого учебного заведения sql 

        private static async Task<(string Top, string Middle, string Bottom)> LoadTransferPageAsync()
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"
                SELECT top_content, middle_text, bottom_content
                FROM transfer_page_content
                WHERE id = 1
            ";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            if (await reader.ReadAsync())
            {
                var top = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var middle = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var bottom = reader.IsDBNull(2) ? "" : reader.GetString(2);
                return (top, middle, bottom);
            }
            return ("", "", "");
        }

        // КОНТАКТЫ 

        private static async Task SendContacts(long chatId, IMaxBotClient api)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Контактная информация:");
            sb.AppendLine();
            sb.AppendLine("ТЕЛЕФОН приемной комиссии: (812) 252-44-47");
            sb.AppendLine();
            sb.AppendLine("Юридический адрес: 197022, Санкт-Петербург, наб. реки Карповки, дом 11а;");
            sb.AppendLine("Часы работы: по рабочим дням 9.00-17.30, обед 13.00-13.30;");
            sb.AppendLine("Приемная директора: (812) 234-23-12;");
            sb.AppendLine("Адрес электронной почты: ktgs@obr.gov.spb.ru");

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString(),
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // ссылка на сайт 

        private static async Task SendKgtcSiteLink(long chatId, IMaxBotClient api)
        {
            var text = "Посетить скайт Колледжа туризма и прикладных технологий:\n" +
                       "https://www.ktgs.ru/inspection/PriemnaaKomissia.php";

            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Вернуться в меню", "back_to_menu"))
            };
            var keyboard = BuildInlineKeyboard(rows);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = text,
                Attachments = new List<Attachment> { keyboard }
            });
        }

        // клавиатура 

        private static InlineKeyboardAttachment BuildInlineKeyboard(IReadOnlyList<List<MessageButton>> rows)
        {
            return new InlineKeyboardAttachment
            {
                Payload = new InlineKeyboardPayload
                {
                    Buttons = rows
                        .Select(list => list.Cast<Button>().ToList())
                        .ToList()
                }
            };
        }

        private static List<MessageButton> Row(MessageButton button)
            => new List<MessageButton> { button };

        private static MessageButton CallbackButton(string text, string payload)
        {
            return new MessageButton
            {
                Text = text
            };
        }
    }
}