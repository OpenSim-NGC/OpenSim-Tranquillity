/* Copyright (c) 2025 Utopia Skye LLC

 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;

namespace OpenSim.Data.Model.Core;

public partial class OsGroupsNotice
{
    public string GroupId { get; set; } = String.Empty;
    public string NoticeId { get; set; } = String.Empty;
    public uint Tmstamp { get; set; }
    public string FromName { get; set; } = String.Empty;
    public string Subject { get; set; } = String.Empty;
    public string Message { get; set; }
    public int HasAttachment { get; set; }
    public int AttachmentType { get; set; }
    public string AttachmentName { get; set; } = String.Empty;
    public string AttachmentItemId { get; set; } = String.Empty;
    public string AttachmentOwnerId { get; set; } = String.Empty;
}
