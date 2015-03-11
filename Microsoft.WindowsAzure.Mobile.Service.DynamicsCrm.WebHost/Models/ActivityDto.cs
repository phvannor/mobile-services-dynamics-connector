﻿using Microsoft.WindowsAzure.Mobile.Service.Tables;
using System;

namespace Microsoft.WindowsAzure.Mobile.Service.DynamicsCrm.WebHost.Models
{
    public class ActivityDto : ITableData
    {
        public DateTimeOffset? CreatedAt { get; set; }
        public bool Deleted { get; set; }
        public string Id { get; set; }
        public DateTimeOffset? UpdatedAt { get; set; }
        public byte[] Version { get; set; }

        public String Subject { get; set; }
        public DateTime? ActualEnd { get; set; }
        public String Details { get; set; }
        public String ActivityTypeCode { get; set; }
        public Guid? RegardingObjectId { get; set; }
    }
}