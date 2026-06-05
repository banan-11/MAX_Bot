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
using System.Net.Mail;
using System.Text;
using System.Threading.Tasks;

namespace Create_max_bot
{
    internal class Program
    {
        // айди чата бота 
        private const long BotChatId = 51951727;

        static async Task Main(string[] args)
        {

            // тут токен от бота для макса
            var token = bot_token.token;

            var client = new MaxBotClient(token);


            // 3. Запускаем получение апдейтов
            var _ = client.PollUpdatesWithCallback(
                async (update, api) =>
                {
                    if (update is MessageCreatedUpdate messageCreated)
                        await HandleMessage(messageCreated, api);

                    if (update is MessageCallbackUpdate callbackUpdate)
                        await HandleCallback(callbackUpdate, api);
                },
                limit: 100,
                timeout: 90,
                types: new List<string>
                {
                        UpdateTypes.MessageCreated,
                        UpdateTypes.MessageCallback
                    // если в библиотеке есть константа типа UpdateTypes.MessageCallback — добавь её сюда
                });

            Console.WriteLine("Бот запущен. Нажми Enter для выхода.");
            Console.ReadLine();




        }

        private static async Task HandleMessage(MessageCreatedUpdate update, IMaxBotClient api)
        {
            var text = update.Message?.Body?.Text ?? string.Empty;
            var chatId = BotChatId;

            if (string.IsNullOrWhiteSpace(text))
                return;

            if (text == "/start")
            {
                await SendMainMenu(chatId, api);
                return;
            }

            // Любой другой текст — показываем главное меню
            await SendMainMenu(chatId, api);
        }


        // обработка нажатий на инлайн кнопочки
        private static async Task HandleCallback(MessageCallbackUpdate update, IMaxBotClient api)
        {
            var payload = update.Callback?.Payload;
            var callbackId = update.Callback?.CallbackId;
            var chatId = BotChatId;

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
                    
                // это что бы не писать кучу кейсов..
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
            var attachments = new[]
            {
                BuildInlineKeyboard(new[]
                {
                    Row(CallbackButton("Даты ДОД", "open_days")),
                    Row(CallbackButton("Специальности", "specialties")),
                    Row(CallbackButton("Корпуса для ДОД", "buildings")),
                    Row(CallbackButton("Срок обучения", "duration")),
                    Row(CallbackButton("Часто задаваемые вопросы", "faq")),
                    Row(CallbackButton("Иностранные граждане", "foreign")),
                    Row(CallbackButton("Сотрудничество с ВУЗами", "universities")),
                    Row(CallbackButton("Перевод из другого учебного заведения", "transfer"))
                })
            };

            var req = new SendMessageRequest
            {
                ChatId = chatId,
                Text = "Выберите интересующий раздел:",
                Format = MessageFormat.Markdown,
                Attachments = attachments
            };

            await api.SendMessageAsync(req);
        }







        // день котрытых дверей
        private static async Task SendOpenDays(long chatId, string callbackId, IMaxBotClient api)
        {
            var items = await LoadOpenDaysAsync();

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Ближайшие дни открытых дверей:");
            sb.AppendLine();

            foreach (var d in items)
                sb.AppendLine($"{d.Id}. {d.Date:dd.MM.yyyy}");

            var msg = new NewMessageBody
            {
                Text = sb.ToString()
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }



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

            var msg = new NewMessageBody
            {
                Text = "Выберите специальность:",
                Attachments = new[]
                {
                    BuildInlineKeyboard(rows)
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
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

            var msg = new NewMessageBody
            {
                Text = sb.ToString(),
                Attachments = new[]
                {
                    BuildInlineKeyboard(new[]
                    {
                        Row(CallbackButton("Назад к списку специальностей", "specialties"))
                    })
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }











        // Коруса для дней открытых дверей
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

            var msg = new NewMessageBody
            {
                Text = "Часто задаваемые вопросы:",
                Attachments = new[]
                {
                    BuildInlineKeyboard(rows)
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
        }

















        // оветы на вопросы FAQ
        private static async Task SendFaqAnswer(long chatId, string callbackId, IMaxBotClient api, int faqId)
        {
            var answer = await LoadFaqAnswerAsync(faqId);

            var msg = new NewMessageBody
            {
                Text = answer ?? "Ответ не найден.",
                Attachments = new[]
                {
                    BuildInlineKeyboard(new[]
                    {
                        Row(CallbackButton("Назад к вопросам", "faq"))
                    })
                }
            };

            await api.AnswerCallbackAsync(new AnswerCallbackRequest
            {
                CallbackId = callbackId,
                Message = msg
            });
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















    }


}

 