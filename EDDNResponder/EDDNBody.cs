﻿using JetBrains.Annotations;
using Newtonsoft.Json;
using System.Collections.Generic;

namespace EDDNResponder
{
    /// <summary>
    /// The body for an EDDN request
    /// </summary>
    class EDDNBody
    {
        [UsedImplicitly] public EDDNHeader header;
        [JsonProperty("$schemaRef")]
        public string schemaRef;
        public IDictionary<string, object> message;
    }
}
