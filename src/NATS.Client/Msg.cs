﻿// Copyright 2015-2020 The NATS Authors
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
// http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Text;
using System.Collections;
using System.Collections.Specialized;

namespace NATS.Client
{
    /// <summary>
    /// The MsgHeaders class provides key/value message header support
    /// simlilar to HTTP headers.
    /// </summary>
    /// <remarks>
    /// This is subclassed from NameValueCollection which is not threadsafe
    /// so concurrent access or modifications may result in undefined behavior.
    /// Only strings are supported for keys and values.
    /// </remarks>
    /// <example>
    /// Setting a header field in a message:
    /// <code>
    /// var m = new Msg();
    /// m.Headers["Content-Type"] = "json";
    /// </code>
    /// 
    /// Getting a header field from a message:
    /// <code>
    /// string contentType = m.Headers["Content-Type"];
    /// </code>
    /// </example>
    public sealed class MsgHeaders : NameValueCollection, IEnumerable
    {
        private static readonly string[] crlf = new string[] { "\r\n" };
        private static readonly char[] kvsep = new char[] { ':' };

        // Message headers are in the form of:
        // |HEADER|crlf|key1:value1|crlf|key2:value2|crlf|...|crlf
        // e.g. MATS/1.0\r\nkey1:value1\r\nkey2:value2\r\n\r\n

        // Define message header version string and size.
        internal static readonly string Header = "NATS/1.0\r\n";
        internal static readonly byte[] HeaderBytes = Encoding.UTF8.GetBytes(Header);
        internal static readonly int    HeaderLen = HeaderBytes.Length;
        internal static readonly int    MinimalValidHeaderLen = Encoding.UTF8.GetBytes(Header + "k:v\r\n\r\n").Length;

        // Cache the serialized headers to optimize reuse
        private byte[] bytes = null;

        /// <summary>
        /// Initializes a new empty instance of the MsgHeaders class.
        /// </summary>
        public MsgHeaders() : base() { }

        /// <summary>
        /// Copies the entries from existing MsgHeaders to a new MsgHeaders
        /// instance.
        /// </summary>
        /// <param name="headers">the NATS message headers to copy.</param>
        public MsgHeaders(MsgHeaders headers) : base(headers) { }

        /// <summary>
        /// Initializes a new instance of the MsgHeaders class.
        /// </summary>
        /// <param name="bytes">A byte array of a serialized MsgHeaders class.</param>
        /// <param name="byteCount">Count of bytes in the serialized array.</param>
        internal MsgHeaders(byte[] bytes, int byteCount) : base()
        {
            if (byteCount < 1)
            {
                throw new NATSException("invalid byte count");
            }
            if (bytes == null)
            {
                throw new NATSException("invalid byte array");
            }
            if (bytes.Length < byteCount)
            {
                throw new NATSException("count exceeeds byte array length");
            }
            if (byteCount < MinimalValidHeaderLen)
            {
                throw new NATSInvalidHeaderException();
            }

            // check for the trailing \r\n\r\n
            if (bytes[byteCount-4] != '\r' || bytes[byteCount - 3] != '\n' ||
                bytes[byteCount-2] != '\r' || bytes[byteCount - 1] != '\n')
            {
                throw new NATSInvalidHeaderException();
            }

            // we are in the fastpath so compare bytes vs a string
            // method.
            for (int i = 0; i < HeaderLen; i++)
            {
                if (bytes[i] != HeaderBytes[i])
                {
                    throw new NATSInvalidHeaderException();
                }
            }

            // Remove the header identifier and trailing crlfs when creating the string
            string kvs = Encoding.UTF8.GetString(bytes, HeaderLen, byteCount - HeaderLen - 4);

            // Split Name Value Pairs
            string[] kvpairs = kvs.Split(crlf, StringSplitOptions.RemoveEmptyEntries);
            if (kvpairs.Length == 0)
            {
                throw new NATSInvalidHeaderException("Empty header");
            }

            foreach (string s in kvpairs)
            {
                string[] nvpair = s.Split(kvsep, StringSplitOptions.RemoveEmptyEntries);
                if (nvpair.Length != 2)
                {
                    throw new NATSInvalidHeaderException("Header field missing key or value.");
                }
                Add(nvpair[0], nvpair[1]);
            }
        }

        /// <summary>
        /// Gets or sets the string entry with the specified string key in the message headers.
        /// </summary>
        /// <param name="name">The string key of the entry to locate. The key can be null.</param>
        /// <returns>A string that contains the comma-separated list of values associated with the specified key, if found; otherwise, null</returns>
        new public string this[string name]
        {
            get
            {
                return base[name];
            }

            set
            {
                base[name] = value;

                // Trigger serialization the next time ToByteArray is called.
                bytes = null;
            }
        }

        private string ToHeaderString()
        {
            // TODO:  optimize based on perf testing
            StringBuilder sb = new StringBuilder(MsgHeaders.Header);
            foreach (string s in this)
            {
                sb.AppendFormat("{0}:{1}\r\n", s, this[s]);
            }
            sb.Append("\r\n");
            return sb.ToString();
        }

        internal byte[] ToByteArray()
        {
            // An empty set of headers should be treated as a message with no
            // headers.
            if (Count == 0)
            {
                return null;
            }

            if (bytes == null)
            {
                bytes = Encoding.UTF8.GetBytes(ToHeaderString());
            }
            return bytes;
        }
    }

    /// <summary>
    /// A NATS message is an object encapsulating a subject, optional reply
    /// payload, optional headers, and subscription information, sent or
    /// received by the client application.
    /// </summary>
    public sealed class Msg
    {
        private static readonly byte[] Empty = new byte[0];
        private string subject;
        private string reply;
        private byte[] data;
        internal Subscription sub;
        internal MsgHeaders headers;

        /// <summary>
        /// Initializes a new instance of the <see cref="Msg"/> class without any
        /// subject, reply, or data.
        /// </summary>
        public Msg()
        {
            subject = null;
            reply = null;
            data = null;
            sub = null;
            headers = null;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Msg"/> class with a subject, reply, headers, and data.
        /// </summary>
        /// <param name="subject">Subject of the message.</param>
        /// <param name="reply">A reply subject, or <c>null</c>.</param>
        /// <param name="headers">Message headers or <c>null</c>.</param>
        /// <param name="data">A byte array containing the message payload.</param>
        public Msg(string subject, string reply, MsgHeaders headers, byte[] data)
        {
            if (string.IsNullOrWhiteSpace(subject))
            {
                throw new ArgumentException(
                    "Subject cannot be null, empty, or whitespace.",
                    "subject");
            }

            this.Subject = subject;
            this.Reply = reply;
            this.Headers = headers;
            this.Data = data;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Msg"/> class with a subject, reply, and data.
        /// </summary>
        /// <param name="subject">Subject of the message.</param>
        /// <param name="reply">A reply subject, or <c>null</c>.</param>
        /// <param name="data">A byte array containing the message payload.</param>
        public Msg(string subject, string reply, byte[] data)
            : this(subject, reply, null, data)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Msg"/> class with a subject and data.
        /// </summary>
        /// <param name="subject">Subject of the message.</param>
        /// <param name="data">A byte array containing the message payload.</param>
        public Msg(string subject, byte[] data)
            : this(subject, null, data)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="Msg"/> class with a subject and no data.
        /// </summary>
        /// <param name="subject">Subject of the message.</param>
        public Msg(string subject)
            : this(subject, null, null)
        {
        }

        internal Msg(MsgArg arg, Subscription s, byte[] payload, long totalLen)
        {
            subject = arg.subject;
            reply = arg.reply;
            sub = s;

            if (arg.hdr > 0)
            {
                headers = new MsgHeaders(payload, (int)arg.hdr);
            }

            // make a deep copy of the bytes for this message.
            if (totalLen > 0)
            {
                data = new byte[totalLen];
                Array.Copy(payload, (int)arg.hdr, data, 0, (int)(totalLen-arg.hdr));
            }
            else
                data = Empty;
        }

        /// <summary>
        /// Gets or sets the subject.
        /// </summary>
        public string Subject
        {
            get { return subject; }
            set { subject = value; }
        }

        /// <summary>
        /// Gets or sets the reply subject.
        /// </summary>
        public string Reply
        {
            get { return reply; }
            set { reply = value; }
        }

        /// <summary>
        /// Gets or sets the payload of the message.
        /// </summary>
        /// <remarks>
        /// This copies application data into the message. See <see cref="AssignData" /> to directly pass the bytes buffer.
        /// </remarks>
        /// <seealso cref="AssignData"/>
        public byte[] Data
        {
            get { return data; }

            set
            {
                if (value == null)
                {
                    this.data = null;
                    return;
                }

                int len = value.Length;
                if (len == 0)
                    this.data = Empty;
                else
                {
                    this.data = new byte[len];
                    Array.Copy(value, 0, data, 0, len);
                }
            }
        }

        /// <summary>
        /// Assigns the data of the message.
        /// </summary>
        /// <remarks>
        /// <para>This is a direct assignment,
        /// to avoid expensive copy operations.  A change to the passed
        /// byte array will be changed in the message.</para>
        /// <para>The calling application is responsible for the data integrity in the message.</para>
        /// </remarks>
        /// <param name="data">a bytes buffer of data.</param>
        public void AssignData(byte[] data)
        {
            this.data = data;
        }

        /// <summary>
        /// Gets the <see cref="ISubscription"/> which received the message.
        /// </summary>
        [ObsoleteAttribute("This property will soon be deprecated. Use ArrivalSubscription instead.")]
        public ISubscription ArrivalSubcription
        {
            get { return sub; }
        }

        /// <summary>
        /// Gets the <see cref="ISubscription"/> which received the message.
        /// </summary>
        public ISubscription ArrivalSubscription
        {
            get { return sub; }
        }

        /// <summary>
        /// Send a response to the message on the arrival subscription.
        /// </summary>
        /// <param name="data">The response payload to send.</param>
        /// <exception cref="NATSException">
        /// <para><see cref="Reply"/> is null or empty.</para>
        /// <para>-or-</para>
        /// <para><see cref="ArrivalSubscription"/> is null.</para>
        /// </exception>
        public void Respond(byte[] data)
        {
            if (String.IsNullOrEmpty(Reply))
            {
                throw new NATSException("No Reply subject");
            }

            Connection conn = ArrivalSubscription?.Connection;
            if (conn == null)
            {
                throw new NATSException("Message is not bound to a subscription");
            }

            conn.Publish(this.Reply, data);
        }

        /// <summary>
        /// Generates a string representation of the messages.
        /// </summary>
        /// <returns>A string representation of the messages.</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            if (headers != null)
            {
                sb.AppendFormat("Headers={0};", headers.ToString());
            }
            sb.AppendFormat("Subject={0};Reply={1};Payload=<", Subject,
                Reply != null ? reply : "null");

            int len = data.Length;
            int i;

            for (i = 0; i < 32 && i < len; i++)
            {
                sb.Append((char)data[i]);
            }

            if (i < len)
            {
                sb.AppendFormat("{0} more bytes", len - i);
            }

            sb.Append(">}");

            return sb.ToString();
        }


        /// <summary>
        /// Gets or sets the <see cref="MsgHeaders"/> of the message.
        /// </summary>
        public MsgHeaders Headers
        {
            get
            {
                // Auto generate the headers if requested from the
                // application.
                if (headers == null)
                {
                    headers = new MsgHeaders();
                }
                return headers;
            }

            set
            {
                headers = value;
            }
        }
    }


}