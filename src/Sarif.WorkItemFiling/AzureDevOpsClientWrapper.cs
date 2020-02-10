﻿// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Newtonsoft.Json;

namespace Microsoft.CodeAnalysis.Sarif.WorkItemFiling
{
    /// <summary>
    /// Represents an Azure DevOps project in which work items can be filed.
    /// </summary>
    public class AzureDevOpsClientWrapper : FilingClient
    {
        private WorkItemTrackingHttpClient _witClient;
        
        public override async Task Connect(string personalAccessToken)
        {
            Uri accountUri = new Uri(this.AccountOrOrganization, UriKind.Absolute);

            VssConnection connection = new VssConnection(accountUri, new VssBasicCredential(string.Empty, personalAccessToken));
            await connection.ConnectAsync();

            _witClient = await connection.GetClientAsync<WorkItemTrackingHttpClient>();
        }

        public override async Task<IEnumerable<WorkItemModel>> FileWorkItems(IEnumerable<WorkItemModel> workItemFilingMetadata)
        {
            foreach (WorkItemModel metadata in workItemFilingMetadata)
            {
                AttachmentReference attachmentReference = null;
                string attachmentText = metadata.Attachment?.Text;
                if (!string.IsNullOrEmpty(attachmentText))
                {
                    using (var stream = new MemoryStream())
                    using (var writer = new StreamWriter(stream))
                    {
                        writer.Write(attachmentText);
                        writer.Flush();
                        stream.Position = 0;
                        try
                        {
                            attachmentReference = await _witClient.CreateAttachmentAsync(stream, fileName: metadata.Attachment.Name);
                        }
                        catch
                        {
                            // TBD error handling
                            throw;
                        }
                    }
                }

                var patchDocument = new JsonPatchDocument
                {
                    new JsonPatchOperation
                    {
                        Operation = Operation.Add,
                        Path = $"/fields/{AzureDevOpsFieldNames.Title}",
                        Value = metadata.Title
                    },
                    new JsonPatchOperation
                    {
                        Operation = Operation.Add,
                        Path = $"/fields/{AzureDevOpsFieldNames.ReproSteps}",
                        Value = metadata.Description
                    },
                    new JsonPatchOperation
                    {
                        Operation = Operation.Add,
                        Path = $"/fields/{AzureDevOpsFieldNames.AreaPath}",
                        Value = metadata.Area
                    },
                    new JsonPatchOperation
                    {
                        Operation = Operation.Add,
                        Path = $"/fields/{AzureDevOpsFieldNames.Tags}",
                        Value = string.Join(",", metadata.Tags)
                    }
                };

                if (metadata.CustomFields != null)
                {
                    foreach (var customField in metadata.CustomFields)
                    {
                        patchDocument.Add(new JsonPatchOperation
                        {
                            Operation = Operation.Add,
                            Path = $"/fields/{customField.Key}",
                            Value = customField.Value
                        });
                    }
                }

                if (attachmentReference != null)
                {
                    patchDocument.Add(
                        new JsonPatchOperation
                        {
                            Operation = Operation.Add,
                            Path = $"/relations/-",
                            Value = new
                            {
                                rel = "AttachedFile",
                                attachmentReference.Url
                            }
                        });
                }

                WorkItem workItem = null;

                try
                {
                    Console.Write($"Creating work item: {metadata.Title}");
                    workItem = await _witClient.CreateWorkItemAsync(patchDocument, project: this.ProjectOrRepository, "Bug");
                    Console.WriteLine($": {workItem.Id}: DONE");
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine(e);

                    if (patchDocument != null)
                    {
                        string patchJson = JsonConvert.SerializeObject(patchDocument, Formatting.Indented);
                        Console.Error.WriteLine(patchJson);
                    }

                    continue;
                }

                const string HTML = "html";
                SarifLog sarifLog = (SarifLog)metadata.AdditionalData;
                foreach (Result result in sarifLog.Runs[0].Results)
                {
                    if (workItem.Links?.Links?.ContainsKey(HTML) == true)
                    {
                        result.WorkItemUris = new List<Uri> { new Uri(((ReferenceLink)workItem.Links.Links[HTML]).Href, UriKind.Absolute) };
                    }
                    else
                    {
                        result.WorkItemUris = new List<Uri> { new Uri(workItem.Url, UriKind.Absolute) };
                    }
                }
            }

            return workItemFilingMetadata;
        }
    }
}