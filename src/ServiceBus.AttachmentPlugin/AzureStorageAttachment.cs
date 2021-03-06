﻿namespace ServiceBus.AttachmentPlugin
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Azure.ServiceBus;
    using Microsoft.Azure.ServiceBus.Core;
    using Microsoft.WindowsAzure.Storage;
    using Microsoft.WindowsAzure.Storage.Blob;

    class AzureStorageAttachment : ServiceBusPlugin
    {
        SemaphoreSlim semaphore= new SemaphoreSlim(1);
        const string MessageId = "_MessageId";
        internal const string ValidUntilUtc = "_ValidUntilUtc";
        internal const string DateFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";

        CloudBlobClient client;
        AzureStorageAttachmentConfiguration configuration;

        public AzureStorageAttachment(AzureStorageAttachmentConfiguration configuration)
        {
            Guard.AgainstNull(nameof(configuration), configuration);
            this.configuration = configuration;
        }

        public override string Name => nameof(AzureStorageAttachment);

        internal static Func<DateTime> DateTimeFunc = () => DateTime.UtcNow;

        public override async Task<Message> BeforeMessageSend(Message message)
        {
            await InitializeClient().ConfigureAwait(false);

            if (!configuration.MessageMaxSizeReachedCriteria(message))
            {
                return message;
            }

            var container = client.GetContainerReference(configuration.ContainerName);
            await container.CreateIfNotExistsAsync().ConfigureAwait(false);
            var blob = container.GetBlockBlobReference(Guid.NewGuid().ToString());

            SetValidMessageId(blob, message.MessageId);
            SetValidUntil(blob, message.TimeToLive);

            await blob.UploadFromByteArrayAsync(message.Body,0, message.Body.Length).ConfigureAwait(false);

            message.Body = null;
            message.UserProperties[configuration.MessagePropertyToIdentifyAttachmentBlob] = blob.Name;

            if (!configuration.SasTokenValidationTime.HasValue)
            {
                return message;
            }

            var sasUri = TokenGenerator.GetBlobSasUri(blob, configuration.SasTokenValidationTime.Value);
            message.UserProperties[configuration.MessagePropertyForSasUri] = sasUri;
            return message;
        }

        async Task InitializeClient()
        {
            if (client != null)
            {
                return;
            }

            await semaphore.WaitAsync().ConfigureAwait(false);

            if (client != null)
            {
                return;
            }

            try
            {
                var connectionString = await configuration.ConnectionStringProvider.GetConnectionString().ConfigureAwait(false);
                var account = CloudStorageAccount.Parse(connectionString);
                client = account.CreateCloudBlobClient();
            }
            finally
            {
                semaphore.Release();
            }
        }

        static void SetValidMessageId(ICloudBlob blob, string messageId)
        {
            if (!string.IsNullOrWhiteSpace(messageId))
            {
                blob.Metadata[MessageId] = messageId;
            }
        }

        static void SetValidUntil(ICloudBlob blob, TimeSpan timeToBeReceived)
        {
            if (timeToBeReceived == TimeSpan.MaxValue)
            {
                return;
            }

            var validUntil = DateTimeFunc().Add(timeToBeReceived);
            blob.Metadata[ValidUntilUtc] = validUntil.ToString(DateFormat);
        }

        public override async Task<Message> AfterMessageReceive(Message message)
        {
            var userProperties = message.UserProperties;

            if (!userProperties.ContainsKey(configuration.MessagePropertyToIdentifyAttachmentBlob))
            {
                return message;
            }

            CloudBlockBlob blob;

            if (configuration.MessagePropertyForSasUri != null && userProperties.ContainsKey(configuration.MessagePropertyForSasUri))
            {
                blob = new CloudBlockBlob(new Uri(userProperties[configuration.MessagePropertyForSasUri].ToString()));
            }
            else
            {
                await InitializeClient().ConfigureAwait(false);

                var container = client.GetContainerReference(configuration.ContainerName);
                await container.CreateIfNotExistsAsync().ConfigureAwait(false);
                var blobName = (string)userProperties[configuration.MessagePropertyToIdentifyAttachmentBlob];
                blob = container.GetBlockBlobReference(blobName);
            }

            try
            {
                await blob.FetchAttributesAsync().ConfigureAwait(false);
            }
            catch (StorageException exception)
            {
                throw new Exception($"Blob with name '{blob.Name}' under container '{blob.Container.Name}' cannot be found."
                    + $" Check {nameof(AzureStorageAttachmentConfiguration)}.{nameof(AzureStorageAttachmentConfiguration.ContainerName)} or"
                    + $" {nameof(AzureStorageAttachmentConfiguration)}.{nameof(AzureStorageAttachmentConfiguration.MessagePropertyToIdentifyAttachmentBlob)} for correct values.", exception);
            }
            var fileByteLength = blob.Properties.Length;
            var bytes = new byte[fileByteLength];
            await blob.DownloadToByteArrayAsync(bytes, 0).ConfigureAwait(false);
            message.Body = bytes;
            return message;
        }
    }
}
