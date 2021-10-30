﻿using System.Web.Http;
using Autofac;
using Microsoft.Bot.Builder.Dialogs;
using RealEstateBot.Dialogs;
using Search.Dialogs;
using System.Configuration;
using System;
using System.Web;
using Microsoft.Bot.Connector;
using Microsoft.Bot.Builder.Dialogs.Internals;
using System.Text.RegularExpressions;
using Microsoft.Bot.Builder.Scorables;
using Microsoft.Bot.Builder.History;
using Microsoft.Bot.Builder.Autofac.Base;
using Microsoft.Bot.Builder.Internals.Fibers;
using System.Linq;
using Microsoft.Bot.Builder.Azure;
using System.Reflection;

namespace RealEstateBot
{
    public class WebApiApplication : HttpApplication
    {

        public static readonly IContainer Container;

        static WebApiApplication()
        {
            var builder = new ContainerBuilder();
            builder.RegisterModule(new DialogModule());
            builder
                // Change the order so that data is loaded before actitivity logger
                .RegisterAdapterChain<IPostToBot>
                (
                    typeof(EventLoopDialogTask),
                    typeof(SetAmbientThreadCulture),
                    typeof(QueueDrainingDialogTask),
                    typeof(LogPostToBot),
                    typeof(PersistentDialogTask),
                    typeof(ExceptionTranslationDialogTask),
                    typeof(SerializeByConversation),
                    typeof(PostUnhandledExceptionToUser)
                )
                .InstancePerLifetimeScope();

            // Add a global scorable to change language to make it easier to do
            var scorable = Actions
                .Bind(async (IActivityLogger ilogger, IBotToUser botToUser, IBotData data, IMessageActivity message) =>
                     {
                         var logger = ilogger as SearchTranslator;
                         var lang = message.Text.Substring(1);
                         if (Search.Utilities.Translator.Languages.Any((l) => l.Locale == lang))
                         {
                             logger.UserLanguage = lang;
                             data.UserData.SetValue("UserLanguage", lang);
                             await botToUser.PostAsync($"Switched language to <literal>{lang}</literal>");
                         }
                         else
                         {
                             await botToUser.PostAsync($"<literal>{lang}</literal> is not a valid locale.");
                         }
                     })
                // TODO: This is the current set of generalnn translation languages
                .When(new Regex(@"^\#"))
                .Normalize();
            builder.RegisterInstance(scorable).AsImplementedInterfaces().SingleInstance();

            var translationKey = ConfigurationManager.AppSettings["TranslationKey"];
            if (string.IsNullOrWhiteSpace(translationKey))
            {
                translationKey = Environment.GetEnvironmentVariable("TranslationKey");
            }

            builder.Register((c) => new SearchTranslator(c.Resolve<ConversationReference>(), c.Resolve<IBotData>(), "en", translationKey))
                   .AsImplementedInterfaces()
                   .InstancePerMatchingLifetimeScope(DialogModule.LifetimeScopeTag)
                   .Keyed<SearchTranslator>(FiberModule.Key_DoNotSerialize);

            builder.RegisterType<RealEstateDialog>()
                .As<IDialog<object>>()
                // .InstancePerDependency();
                .InstancePerMatchingLifetimeScope(DialogModule.LifetimeScopeTag);

            Container = builder.Build();
        }

        protected void Application_Start()
        {

            Conversation.UpdateContainer(builder =>
            {
                builder.RegisterModule(new AzureModule(Assembly.GetExecutingAssembly()));
                var store = new InMemoryDataStore();
                // var store = new TableBotDataStore(ConfigurationManager.ConnectionStrings["StorageConnectionString"].ConnectionString);

                builder.Register(c => store)
                    .Keyed<IBotDataStore<BotData>>(AzureModule.Key_DataStore)
                    .AsSelf()
                    .SingleInstance();
            });

            GlobalConfiguration.Configure(WebApiConfig.Register);
        }
    }
}