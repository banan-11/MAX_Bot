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
        private static readonly string ConnectionString =
            "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=14102008_vld";

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
                                try
                                {
                                    await HandleMessage(messageCreated, api);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"[ERROR] HandleMessage: {ex}");
                                }
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
                            Console.WriteLine($"[WATCHDOG] Нет апдейтов {idle.TotalSeconds:F0} сек. Перезапуск клиента...");
                            break;
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка в Main/polling: {ex}");
                    Console.WriteLine("Перезапуск через 5 секунд...");
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
        }

        // ===== user_id -> chat_id =====

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

        // ===== Обработка сообщений =====

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
                Console.WriteLine($"[WARN] ChatId for user {userId} not found in DB");
                return;
            }

            if (text == "/start" || text.Equals("Начать", StringComparison.OrdinalIgnoreCase))
            {
                await SendMainMenu(chatId, api);
                return;
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

            switch (text)
            {
                case "Даты Дней открытых дверей":
                    await SendOpenDays(chatId, api);
                    break;

                case "Специальности":
                    await SendSpecialties(chatId, api);
                    break;

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

                default:
                    Console.WriteLine("Я ничего не понял :((((");
                    break;
            }
        }

        // ===== Меню =====

        private static async Task SendMainMenu(long chatId, IMaxBotClient api)
        {
            var rows = new List<List<MessageButton>>
            {
                Row(CallbackButton("Даты Дней открытых дверей", "open_days")),
                Row(CallbackButton("Специальности", "specialties")),
                Row(CallbackButton("Корпуса для ДОД", "buildings")),
                Row(CallbackButton("Срок обучения", "duration")),
                Row(CallbackButton("Часто задаваемые вопросы", "faq")),
                Row(CallbackButton("Иностранные граждане", "foreign")),
                Row(CallbackButton("Сотрудничество с ВУЗами", "universities")),
                Row(CallbackButton("Перевод из другого учебного заведения", "transfer")),
                Row(CallbackButton("посетить сайт кгтс", "kgtc_site"))
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

        // ===== Дни открытых дверей =====

        private static async Task SendOpenDays(long chatId, IMaxBotClient api)
        {
            var items = await LoadOpenDaysAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Ближайшие дни открытых дверей:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine($"{d.Id}. {d.Date:dd.MM.yyyy}");

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
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

        // ===== Список специальностей =====

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

            sb.AppendLine();
            sb.AppendLine("Чтобы получить подробную информацию, введите:");
            sb.AppendLine("Спец и номер интересующей специальности (например: Спец 2)");

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
            });
        }

        private static async Task<List<(int Id, string Cod, string Title)>> LoadSpecialtiesAsync()
        {
            var result = new List<(int, string, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT id, cod, title FROM specialties_list ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
            }
            return result;
        }

        // информация о специальности + срок =====

        private static async Task SendSpecialtyDetails(long chatId, IMaxBotClient api, int specialtyId)
        {
            var details = await LoadSpecialtyDetailsAsync(specialtyId);
            var duration = await LoadDurationForSpecialtyAsync(specialtyId);

            if (details.Count == 0 && string.IsNullOrWhiteSpace(duration))
            {
                await api.SendMessageAsync(new SendMessageRequest
                {
                    ChatId = chatId,
                    Text = "Информация по этой специальности пока не заполнена."
                });
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Подробная информация по специальности №{specialtyId}:");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(duration))
            {
                sb.AppendLine("Срок обучения:");
                sb.AppendLine("• " + duration);
                sb.AppendLine();
            }

            foreach (var line in details)
            {
                sb.AppendLine("• " + line);
                sb.AppendLine();
            }

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
            });
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

            return string.Join("\n• ", list);
        }

        // корпуса 

        private static async Task SendBuildings(long chatId, IMaxBotClient api)
        {
            var items = await LoadBranchesAsync();

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

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
            });
        }

        private static async Task<List<(string BranchName, string Address, string Metro)>> LoadBranchesAsync()
        {
            var result = new List<(string, string, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT branch_name, adress, metro_station FROM college_branches ORDER BY id";
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                result.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
            return result;
        }

        // ===== Общий список сроков =====

        private static async Task SendDuration(long chatId, IMaxBotClient api)
        {
            var items = await LoadBasicEducationAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Сроки обучения:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine("• " + d);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
            });
        }

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

        // ===== FAQ =====

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

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
            });
        }

        private static async Task<List<(int Id, string Question)>> LoadFaqTitlesAsync(int admissionId)
        {
            var result = new List<(int, string)>();
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT id, question FROM admission_faq WHERE admission_id = @adm AND display_order > 0 ORDER BY display_order";
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

        // ===== Инфоблоки (иностранцы / ВУЗы) =====

        private static async Task SendInfoBlock(long chatId, IMaxBotClient api, string infoType)
        {
            var content = await LoadInformationStatAsync(infoType);

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = content
            });
        }

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

            const string sql = "SELECT content FROM information_stat WHERE specialty_id = 1 AND title LIKE @title ORDER BY id LIMIT 1";
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

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = sb.ToString()
            });
        }

        // перевод из другого учебного заведения sql
        private static async Task<(string Top, string Middle, string Bottom)> LoadTransferPageAsync()
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = "SELECT top_content, middle_text, bottom_content FROM transfer_page_content WHERE id = 1";
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

        // ссылка на сайт 

        private static async Task SendKgtcSiteLink(long chatId, IMaxBotClient api)
        {
            var text = "Перейти на сайт приёмной комиссии КГТС:\n" +
                       "https://www.ktgs.ru/inspection/PriemnaaKomissia.php";

            await api.SendMessageAsync(new SendMessageRequest
            {
                ChatId = chatId,
                Text = text
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