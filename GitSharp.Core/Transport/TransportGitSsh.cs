/*
 * Copyright (C) 2008, Robin Rosenberg <robin.rosenberg@dewire.com>
 * Copyright (C) 2008, Shawn O. Pearce <spearce@spearce.org>
 * Copyright (C) 2008, Marek Zawirski <marek.zawirski@gmail.com>
 *
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or
 * without modification, are permitted provided that the following
 * conditions are met:
 *
 * - Redistributions of source code must retain the above copyright
 *   notice, this list of conditions and the following disclaimer.
 *
 * - Redistributions in binary form must reproduce the above
 *   copyright notice, this list of conditions and the following
 *   disclaimer in the documentation and/or other materials provided
 *   with the distribution.
 *
 * - Neither the name of the Git Development Community nor the
 *   names of its contributors may be used to endorse or promote
 *   products derived from this software without specific prior
 *   written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES
 * OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT
 * NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
 * CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT,
 * STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF
 * ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using GitSharp.Core.Exceptions;
using GitSharp.Core.Util;
using Tamir.SharpSsh.jsch;

namespace GitSharp.Core.Transport
{
    public class TransportGitSsh : SshTransport, IPackTransport, IDisposable
    {
        public static bool canHandle(URIish uri)
        {
            if (!uri.IsRemote)
            {
                return false;
            }

            string scheme = uri.Scheme;

            if ("ssh".Equals(scheme))
            {
                return true;
            }

            if ("ssh+git".Equals(scheme))
            {
                return true;
            }

            if ("git+ssh".Equals(scheme))
            {
                return true;
            }

            if (scheme == null && uri.Host != null && uri.Path != null)
            {
                return true;
            }

            return false;
        }

        private Stream _errStream;

        public TransportGitSsh(Repository local, URIish uri)
            : base(local, uri)
        {
        }

        public override IFetchConnection openFetch()
        {
            return new SshFetchConnection(this);
        }

        public override IPushConnection openPush()
        {
            return new SshPushConnection(this);
        }

        private static void SqMinimal(StringBuilder cmd, string val)
        {
            if (Regex.Matches(val, "^[a-zA-Z0-9._/-]*$").Count > 0)
            {
                cmd.Append(val);
            }
            else
            {
                Sq(cmd, val);
            }
        }

        private static void SqAlways(StringBuilder cmd, string val)
        {
            Sq(cmd, val);
        }

        private static void Sq(StringBuilder cmd, string val)
        {
            if (val.Length > 0)
            {
                cmd.Append(QuotedString.BOURNE.quote(val));
            }
        }

        private ChannelExec Exec(string exe)
        {
            InitSession();

            try
            {
                var channel = (ChannelExec)Sock.openChannel("exec");
                string path = Uri.Path;
                if (Uri.Scheme != null && Uri.Path.StartsWith("/~"))
                {
                    path = (Uri.Path.Substring(1));
                }

                var cmd = new StringBuilder();
                int gitspace = exe.IndexOf("git ");
                if (gitspace >= 0)
                {
                    SqMinimal(cmd, exe.Slice(0, gitspace + 3));
                    cmd.Append(' ');
                    SqMinimal(cmd, exe.Substring(gitspace + 4));
                }
                else
                {
                    SqMinimal(cmd, exe);
                }

                cmd.Append(' ');
                SqAlways(cmd, path);
                channel.setCommand(cmd.ToString());
                _errStream = CreateErrorStream();
                channel.setErrStream(_errStream);
                channel.connect();
                return channel;
            }
            catch (JSchException e)
            {
                throw new TransportException(Uri, e.Message, e);
            }
        }

        private static Stream CreateErrorStream()
        {
            return new GitSshErrorStream();
        }

        public NoRemoteRepositoryException cleanNotFound(NoRemoteRepositoryException nf)
        {
            string why = _errStream.ToString();
            if (string.IsNullOrEmpty(why))
            {
                return nf;
            }

            string path = Uri.Path;
            if (Uri.Scheme != null && Uri.Path.StartsWith("/~"))
            {
                path = Uri.Path.Substring(1);
            }

            var pfx = new StringBuilder();
            pfx.Append("fatal: ");
            SqAlways(pfx, path);
            pfx.Append(":");
            if (why.StartsWith(pfx.ToString()))
            {
                why = why.Substring(pfx.Length);
            }

            return new NoRemoteRepositoryException(Uri, why);
        }

        #region Nested Types

        private class GitSshErrorStream : MemoryStream
        {
            private readonly StringBuilder _all = new StringBuilder();

            public override void Write(byte[] buffer, int offset, int count)
            {
                for (int i = offset; i < count + offset; i++)
                {
                    if (buffer[i] == '\n')
                    {
                        string line = Constants.CHARSET.GetString(ToArray());
                        _all.AppendLine(line);
                        SetLength(0);
                        Write(buffer, offset + (i - offset), count - (i - offset));
                        return;
                    }
                    WriteByte(buffer[i]);
                }
                base.Write(buffer, offset, count);
            }

            public override string ToString()
            {
                return _all + "\n" + Constants.CHARSET.GetString(ToArray());
            }
        }

        private class SshFetchConnection : BasePackFetchConnection
        {
            private ChannelExec _channel;

            public SshFetchConnection(TransportGitSsh instance)
                : base(instance)
            {
                try
                {
                    _channel = instance.Exec(instance.OptionUploadPack);

                    if (_channel.isConnected())
                        init(_channel.getOutputStream());
                    else
                        throw new TransportException(uri, instance._errStream.ToString());
                }
                catch (TransportException)
                {
                    Close();
                    throw;
                }
                catch (SocketException err)
                {
                    Close();
                    throw new TransportException(uri, "remote hung up unexpectedly", err);
                }

                try
                {
                    readAdvertisedRefs();
                }
                catch (NoRemoteRepositoryException notFound)
                {
                    throw instance.cleanNotFound(notFound);
                }
            }

            public override void Close()
            {
                base.Close();

                if (_channel == null) return;

                try
                {
                    if (_channel.isConnected())
                    {
                        _channel.disconnect();
                    }
                }
                finally
                {
                    _channel = null;
                }
            }
        }

        private class SshPushConnection : BasePackPushConnection
        {
            private ChannelExec _channel;

            public SshPushConnection(TransportGitSsh instance)
                : base(instance)
            {
                try
                {
                    _channel = instance.Exec(instance.OptionReceivePack);

                    if (_channel.isConnected())
                    {
                        init(_channel.getOutputStream());
                    }
                    else
                    {
                        throw new TransportException(uri, instance._errStream.ToString());
                    }
                }
                catch (TransportException)
                {
                    Close();
                    throw;
                }
                catch (SocketException err)
                {
                    Close();
                    throw new TransportException(uri, "remote hung up unexpectedly", err);
                }

                try
                {
                    readAdvertisedRefs();
                }
                catch (NoRemoteRepositoryException notFound)
                {
                    throw instance.cleanNotFound(notFound);
                }
            }

            public override void Close()
            {
                base.Close();

                if (_channel != null)
                {
                    try
                    {
                        if (_channel.isConnected())
                        {
                            _channel.disconnect();
                        }
                    }
                    finally
                    {
                        _channel = null;
                    }
                }
            }
        }

        #endregion

        public override void Dispose()
        {
            _errStream.Dispose();
            base.Dispose();
        }
    }
}
