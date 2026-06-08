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

using System.Text;
using System.Threading.Tasks;
using System.Linq;

namespace Create_max_bot
{

 



    internal class Program
    {
        // айди чата бота 
        private const long BotChatId = 51951727;


        // строка подключения 
        private static readonly string ConnectionString = "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=14102008_vld";


        static async Task Main(string[] args)
        {
            var token = bot_token.token;
            var client = new MaxBotClient(token);

            Console.WriteLine("Старт PollUpdates...");

            var _ = client.PollUpdatesWithCallback(
            async (update, api) =>
            {
                Console.WriteLine($"[UPDATE] type={update.UpdateType}");

                if (update is MessageCreatedUpdate messageCreated)
                {
                    await HandleMessage(messageCreated, api);
                }
                else if (update is MessageCallbackUpdate callbackUpdate)
                {
                    await HandleCallback(callbackUpdate, api);
                }
            },

                limit: 100,
                timeout: 90,
                types: new List<string>
                {
                    UpdateTypes.MessageCreated,
                    UpdateTypes.MessageCallback  // обязательно добавь это
                });

            Console.WriteLine("Бот запущен. Нажми Enter для выхода.");
            Console.ReadLine();
        }





        private static async Task HandleMessage(MessageCreatedUpdate update, IMaxBotClient api)
        {
            var text = update.Message?.Body?.Text ?? string.Empty;
            var chatId = BotChatId;

            Console.WriteLine($"[MESSAGE] text='{text}'");

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (text == "/start")
            {
                await SendMainMenu(chatId, api);
            }
            else if (text == "Даты Дней открытых дверей")
            {
                await SendOpenDays(chatId, callbackId: null, api);
            }
        }


        // обработка нажатий на инлайн кнопочки

        private static async Task HandleCallback(MessageCallbackUpdate update, IMaxBotClient api)
        {
            var payload = update.Callback?.Payload;
            var callbackId = update.Callback?.CallbackId;
            // У тебя в MessageCallbackUpdate НЕТ ChatId в базовом классе,
            // поэтому временно используем тот же чат, что и в обычных сообщениях:
            var chatId = BotChatId;

            Console.WriteLine($"[CALLBACK] payload='{payload}', callbackId='{callbackId}'");

            if (chatId == 0 || string.IsNullOrEmpty(payload) || string.IsNullOrEmpty(callbackId))
                return;

            switch (payload)
            {
                case "open_days":
                    await SendOpenDays(chatId, callbackId, api);
                    break;

                case "specialties":
                    await SendSpecialties(chatId, callbackId, api);
                    break;

                case "buildings":
                    await SendBuildings(chatId, callbackId, api);
                    break;

                case "duration":
                    await SendDuration(chatId, callbackId, api);
                    break;

                case "faq":
                    await SendFaqMenu(chatId, callbackId, api);
                    break;

                case "foreign":
                    await SendInfoBlock(chatId, callbackId, api, infoType: "foreign");
                    break;

                case "universities":
                    await SendInfoBlock(chatId, callbackId, api, infoType: "universities");
                    break;

                case "transfer":
                    await SendTransferInfo(chatId, callbackId, api);
                    break;

                default:
                    // FAQ
                    if (payload.StartsWith("faq_") && int.TryParse(payload[4..], out var faqId))
                        await SendFaqAnswer(chatId, callbackId, api, faqId);

                    // Специальности
                    if (payload.StartsWith("spec_") && int.TryParse(payload[5..], out var specId))
                        await SendSpecialtyDetails(chatId, callbackId, api, specId);

                break;
            }
        }

        // основная менюшка
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
                 Row(CallbackButton("Перевод из другого учебного заведения", "transfer"))
            };

            var keyboard = BuildInlineKeyboard(rows);

            var req = new SendMessageRequest
            {
                ChatId = chatId,
                Text = "Выберите интересующий раздел:",
                Format = MessageFormat.Markdown,
                Attachments = new List<Attachment>
                {
                    keyboard
                }
            };

            await api.SendMessageAsync(req);
        }






        // день котрытых дверей
        private static async Task SendOpenDays(long chatId, string? callbackId, IMaxBotClient api)
        {
            var items = await LoadOpenDaysAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Ближайшие дни открытых дверей:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine($"{d.Id}. {d.Date:dd.MM.yyyy}");

            if (!string.IsNullOrEmpty(callbackId))
            {
                // вариант для callback-кнопки
                await api.AnswerCallbackAsync(new AnswerCallbackRequest
                {
                    CallbackId = callbackId,
                    Message = new NewMessageBody { Text = sb.ToString() }
                });
            }
            else
            {
                // вариант для обычного сообщения
                await api.SendMessageAsync(new SendMessageRequest
                {
                    ChatId = chatId,
                    Text = sb.ToString()
                });
            }
        }


        // Дни открытых дверей SQL
        private static async Task<List<(int Id, DateTime Date)>> LoadOpenDaysAsync()
        {
            var result = new List<(int, DateTime)>();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT id, even_date
                                 FROM open_door_time
                                 ORDER BY even_date";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var date = reader.GetDateTime(1);
                result.Add((id, date));
            }

            return result;
        }




        // список специальностей 
        private static async Task SendSpecialties(long chatId, string callbackId, IMaxBotClient api)
        {
            var specialties = await LoadSpecialtiesAsync();

            var rows = new List<List<MessageButton>>();

            foreach (var s in specialties)
            {
                var text = $"{s.Cod} — {s.Title}";
                var payload = $"spec_{s.Id}";
                rows.Add(Row(CallbackButton(text, payload)));
            }

            var keyboard = BuildInlineKeyboard(rows);

            var msg = new NewMessageBody
            {
                Text = "Выберите специальность:",
                Attachments = new List<Attachment>
                {
                    keyboard
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }


        // список специальностей SQL
        private static async Task<List<(int Id, string Cod, string Title)>> LoadSpecialtiesAsync()
        {
            var result = new List<(int, string, string)>();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT id, cod, title
                                 FROM specialties_list
                                 ORDER BY id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var id = reader.GetInt32(0);
                var cod = reader.GetString(1);
                var title = reader.GetString(2);
                result.Add((id, cod, title));
            }

            return result;
        }





        // описание специальностей 
        private static async Task SendSpecialtyDetails(long chatId, string callbackId, IMaxBotClient api, int specialtyId)
        {
            var details = await LoadSpecialtyDetailsAsync(specialtyId);

            if (details.Count == 0)
            {
                await api.AnswerCallbackAsync(new AnswerCallbackRequest
                {
                    CallbackId = callbackId,
                    Message = new NewMessageBody { Text = "Информация по этой специальности пока не заполнена." }
                });
                return;
            }

            var sb = new StringBuilder();
            foreach (var line in details)
            {
                sb.AppendLine("• " + line);
                sb.AppendLine();
            }

            var buttons = new List<List<MessageButton>>
            {
                 Row(CallbackButton("Назад к списку специальностей", "specialties"))
            };

            var keyboard = BuildInlineKeyboard(buttons);

            var msg = new NewMessageBody
            {
                Text = sb.ToString(),
                Attachments = new List<Attachment>
        {
            keyboard
        }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }


        // описание специальностей SQL
        private static async Task<List<string>> LoadSpecialtyDetailsAsync(int specialtyId)
        {
            var result = new List<string>();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT content
                                 FROM filling_in_data_for_specializations
                                 WHERE specialty_id = @id
                                 ORDER BY id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", specialtyId);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var content = reader.GetString(0);
                result.Add(content);
            }

            return result;
        }









        // Коруса 
        private static async Task SendBuildings(long chatId, string callbackId, IMaxBotClient api)
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

            var msg = new NewMessageBody { Text = sb.ToString() };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }



        // корпуса SQL
        private static async Task<List<(string BranchName, string Address, string Metro)>> LoadBranchesAsync()
        {
            var result = new List<(string, string, string)>();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT branch_name, adress, metro_station
                                 FROM college_branches
                                 ORDER BY id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                var addr = reader.GetString(1);
                var metro = reader.GetString(2);
                result.Add((name, addr, metro));
            }

            return result;
        }





        // срок обучения 
        private static async Task SendDuration(long chatId, string callbackId, IMaxBotClient api)
        {
            var items = await LoadBasicEducationAsync();

            var sb = new StringBuilder();
            sb.AppendLine("Сроки обучения:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine("• " + d);

            var msg = new NewMessageBody { Text = sb.ToString() };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }




        // срок обучения SQL
        private static async Task<List<string>> LoadBasicEducationAsync()
        {
            var result = new List<string>();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT education_info
                                 FROM basic_education
                                 ORDER BY id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync();

            while (await reader.ReadAsync())
            {
                var info = reader.GetString(0);
                result.Add(info);
            }

            return result;
        }





        // FAQ
        // FAQ
        private static async Task SendFaqMenu(long chatId, string callbackId, IMaxBotClient api)
        {
            // У тебя сейчас один admission_info с id = 1
            var faqItems = await LoadFaqTitlesAsync(admissionId: 1);

            var rows = new List<List<MessageButton>>();

            foreach (var item in faqItems)
            {
                var payload = $"faq_{item.Id}";
                rows.Add(Row(CallbackButton(item.Question, payload)));
            }

            var keyboard = BuildInlineKeyboard(rows);

            var msg = new NewMessageBody
            {
                Text = "Часто задаваемые вопросы:",
                Attachments = new List<Attachment>
                {
                     keyboard
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }




        //  FAQ SQL
        private static async Task<List<(int Id, string Question)>> LoadFaqTitlesAsync(int admissionId)
        {
            var result = new List<(int, string)>();

            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT id, question
                                 FROM admission_faq
                                 WHERE admission_id = @adm
                                   AND display_order > 0
                                 ORDER BY display_order";

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




        // оветы на вопросы FAQ
        private static async Task SendFaqAnswer(long chatId, string callbackId, IMaxBotClient api, int faqId)
        {
            var answer = await LoadFaqAnswerAsync(faqId);

            var buttons = new List<List<MessageButton>>
            {
                Row(CallbackButton("Назад к вопросам", "faq"))
            };

            var keyboard = BuildInlineKeyboard(buttons); // это InlineKeyboardAttachment

            var msg = new NewMessageBody
            {
                Text = answer ?? "Ответ не найден.",
                Attachments = new List<Attachment>
                {
                     keyboard
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }



        // Ответ на вопросы FAQ SQL
        private static async Task<string?> LoadFaqAnswerAsync(int faqId)
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT answer
                                 FROM admission_faq
                                 WHERE id = @id";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", faqId);

            var result = await cmd.ExecuteScalarAsync();
            return result as string;
        }





        // инфа о примеме иностранных граждан
        private static async Task SendInfoBlock(long chatId, string callbackId, IMaxBotClient api, string infoType)
        {
            var content = await LoadInformationStatAsync(infoType);

            var msg = new NewMessageBody { Text = content };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }



        // инфа о примеме иностранных граждан slq
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

            const string sql = @"SELECT content
                                 FROM information_stat
                                 WHERE specialty_id = 1
                                   AND title LIKE @title
                                 ORDER BY id
                                 LIMIT 1";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("title", filter + "%");

            var result = await cmd.ExecuteScalarAsync();
            return result as string ?? "Информация временно недоступна.";
        }



        // перевод из другого учебного заведения 
        private static async Task SendTransferInfo(long chatId, string callbackId, IMaxBotClient api)
        {
            var (top, middle, bottom) = await LoadTransferPageAsync();

            var sb = new StringBuilder();
            sb.AppendLine(top);
            sb.AppendLine();
            sb.AppendLine(middle);
            sb.AppendLine();
            sb.AppendLine(bottom);

            var msg = new NewMessageBody { Text = sb.ToString() };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }


        // перевод из другого учебного заведения SQL
        private static async Task<(string Top, string Middle, string Bottom)> LoadTransferPageAsync()
        {
            await using var conn = new NpgsqlConnection(ConnectionString);
            await conn.OpenAsync();

            const string sql = @"SELECT top_content, middle_text, bottom_content
                                 FROM transfer_page_content
                                 WHERE id = 1";

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
                // payload сюда НЕ кладём, у MessageButton его нет
            };
        }







    }


}

 