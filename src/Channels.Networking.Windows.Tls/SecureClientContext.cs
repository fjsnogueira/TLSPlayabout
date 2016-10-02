﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Channels.Networking.Windows.Tls.Internal;
using static Channels.Networking.Windows.Tls.Internal.InteropEnums;

namespace Channels.Networking.Windows.Tls
{
    public unsafe class SecureClientContext: IDisposable
    {
        EncryptionPolicy _encryptPolicy;
        string _hostName;
        bool _remoteCertRequired = false;
        bool _checkCertName = false;
        bool _checkCertRevocationStatus = false;
        EncryptionPolicy _encryptionPolicy = EncryptionPolicy.RequireEncryption;
        SspiGlobal _securityContext;
        SSPIHandle _contextPointer;
        private int _headerSize = 5; //5 is the minimum (1 for frame type, 2 for version, 2 for frame size)
        private int _trailerSize = 16;
        private int _maxDataSize = 16354;
        private bool _readerToSend;
        private ApplicationProtocols.ProtocolIds _negotiatedProtocol;
        public bool ReaderToSend => _readerToSend;
        public ApplicationProtocols.ProtocolIds NegotiatedProtocol => _negotiatedProtocol;

        public SecureClientContext(SspiGlobal securityContext, string hostName)
        {
            _securityContext = securityContext;
            _encryptPolicy = EncryptionPolicy.RequireEncryption;

            if (hostName == null)
            {
                throw new ArgumentNullException(nameof(hostName));
            }
            _hostName = hostName;
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        

        public byte[] ProcessContextMessage(ReadableBuffer messageBuffer)
        {
            if (!messageBuffer.IsSingleSpan)
            {
                throw new NotImplementedException();
            }
            SecurityBufferDescriptor output = new SecurityBufferDescriptor(2);
            SecurityBuffer* outputBuff = stackalloc SecurityBuffer[2];
            outputBuff[0].size = 0;
            outputBuff[0].tokenPointer = null;
            outputBuff[0].type = SecurityBufferType.Token;
            outputBuff[1].type = SecurityBufferType.Alert;
            outputBuff[1].size = 0;
            outputBuff[1].tokenPointer = null;

            output.UnmanagedPointer = outputBuff;

            var handle = _securityContext.CredentialsHandle;
            SSPIHandle localhandle = _contextPointer;
            void* contextptr;
            void* newContextptr;
            if (_contextPointer.handleHi == IntPtr.Zero && _contextPointer.handleLo == IntPtr.Zero)
            {
                contextptr = null;
                newContextptr = &localhandle;
            }
            else
            {
                contextptr = &localhandle;
                newContextptr = null;
            }

            ContextFlags unusedAttributes = default(ContextFlags);
            SecurityBufferDescriptor* pointerToDescriptor = null;
            if (messageBuffer.Length > 0)
            {
                SecurityBufferDescriptor input = new SecurityBufferDescriptor(2);
                SecurityBuffer* inputBuff = stackalloc SecurityBuffer[2];
                inputBuff[0].size = messageBuffer.Length;

                
                if (messageBuffer.IsSingleSpan)
                {
                    void* arrayPointer;
                    messageBuffer.First.TryGetPointer(out arrayPointer);
                    inputBuff[0].tokenPointer = arrayPointer;
                }
                else
                {
                    byte* tempBuffer = stackalloc byte[messageBuffer.Length];
                    Span<byte> tmpSpan = new Span<byte>(tempBuffer,messageBuffer.Length);
                    messageBuffer.CopyTo(tmpSpan);
                    inputBuff[0].tokenPointer = tempBuffer;
                }
                                

                inputBuff[0].type = SecurityBufferType.Token;

                outputBuff[1].type = SecurityBufferType.Empty;
                outputBuff[1].size = 0;
                outputBuff[1].tokenPointer = null;

                input.UnmanagedPointer = inputBuff;
                pointerToDescriptor = &input;
                
            }
            else
            {
                if (_securityContext.LengthOfSupportedProtocols > 0)
                {
                    SecurityBufferDescriptor input = new SecurityBufferDescriptor(1);
                    SecurityBuffer* inputBuff = stackalloc SecurityBuffer[1];
                    inputBuff[0].size = _securityContext.LengthOfSupportedProtocols;

                    inputBuff[0].tokenPointer =(void*) _securityContext.AlpnSupportedProtocols;

                    inputBuff[0].type = SecurityBufferType.ApplicationProtocols;

                    input.UnmanagedPointer = inputBuff;
                    pointerToDescriptor = &input;
                }
            }
            
            long timestamp = 0;
            SecurityStatus errorCode = (SecurityStatus) InteropSspi.InitializeSecurityContextW(ref handle, contextptr, _hostName, SspiGlobal.RequiredFlags | ContextFlags.InitManualCredValidation,0, Endianness.Native, pointerToDescriptor,0,  newContextptr, output, ref unusedAttributes, out timestamp);

            _contextPointer = localhandle;
           
            if (errorCode == SecurityStatus.ContinueNeeded || errorCode == SecurityStatus.OK)
            {
                byte[] outArray = null;
                if(outputBuff[0].size > 0)
                {
                    outArray = new byte[outputBuff[0].size];
                    Marshal.Copy((IntPtr)outputBuff[0].tokenPointer, outArray, 0, outputBuff[0].size);
                    InteropSspi.FreeContextBuffer((IntPtr)outputBuff[0].tokenPointer);
                }
                if (errorCode == SecurityStatus.OK)
                {
                    StreamSizes ss;
                    //We have a valid context so lets query it for info
                    InteropSspi.QueryContextAttributesW(ref _contextPointer, ContextAttribute.StreamSizes, out ss);
                    _headerSize = ss.header;
                    _trailerSize = ss.trailer;
                    
                    if (_securityContext.LengthOfSupportedProtocols > 0)
                    {
                        SecPkgContext_ApplicationProtocol protoInfo;

                        InteropSspi.QueryContextAttributesW(ref _contextPointer, ContextAttribute.ApplicationProtocol, out protoInfo);

                        if(protoInfo.ProtoNegoStatus != SEC_APPLICATION_PROTOCOL_NEGOTIATION_STATUS.SecApplicationProtocolNegotiationStatus_Success)
                        {
                            throw new InvalidOperationException("Could not negotiate a mutal application protocol");
                        }
                        _negotiatedProtocol = ApplicationProtocols.GetNegotiatedProtocol(protoInfo.ProtocolId, protoInfo.ProtocolIdSize);
                    }
                    _readerToSend = true;
                }
                return outArray;
            }
            
            throw new InvalidOperationException($"An error occured trying to negoiate a session {errorCode}");
        }

        
        public void Encrypt(WritableBuffer outBuffer, ReadableBuffer buffer)
        {
            outBuffer.Ensure(_trailerSize + _headerSize + buffer.Length);
            void* outBufferPointer;
            outBuffer.Memory.TryGetPointer(out outBufferPointer);

            buffer.CopyTo(outBuffer.Memory.Slice(_headerSize, buffer.Length));

            var securityBuff = stackalloc SecurityBuffer[4];
            SecurityBufferDescriptor sdcInOut = new SecurityBufferDescriptor(4);
            securityBuff[0].size = _headerSize;
            securityBuff[0].type = SecurityBufferType.Header;
            securityBuff[0].tokenPointer = outBufferPointer;

            securityBuff[1].size = buffer.Length;
            securityBuff[1].type = SecurityBufferType.Data;
            securityBuff[1].tokenPointer = (byte*)outBufferPointer + _headerSize;

            securityBuff[2].size = _trailerSize;
            securityBuff[2].type = SecurityBufferType.Trailer;
            securityBuff[2].tokenPointer = (byte*)outBufferPointer + _headerSize + buffer.Length;

            securityBuff[3].size = 0;
            securityBuff[3].tokenPointer = null;
            securityBuff[3].type = SecurityBufferType.Empty;

            sdcInOut.UnmanagedPointer = securityBuff;

            var result = (SecurityStatus)InteropSspi.EncryptMessage(ref _contextPointer, 0, sdcInOut, 0);
            if (result == 0)
            {
                outBuffer.Advance(_headerSize + _trailerSize + buffer.Length);
            }
            else
            {
                throw new InvalidOperationException("BlaBla");
            }

        }


        public unsafe SecurityStatus Decrypt(ReadableBuffer buffer, out ReadableBuffer decryptedData)
        {
            void* pointer;
            
            if (buffer.IsSingleSpan)
            {
                buffer.First.TryGetPointer(out pointer);
            }
            else
            {
                byte* tmpBuffer = stackalloc byte[buffer.Length];
                Span<byte> span = new Span<byte>(tmpBuffer,buffer.Length);
                buffer.CopyTo(span);
                pointer = tmpBuffer;
            }

            decryptedData = buffer;
            int offset = 0;
            int count = buffer.Length;

            var secStatus = DecryptMessage(pointer, ref offset, ref count);
            decryptedData = buffer.Slice(offset, count);
            //if (needsToWriteBack)
            //{
            //    var actualData = memory.Slice().Slice(offset,count);
            //    decryptedData.FirstSpan.Write(actualData);
            //}
            return secStatus;
        }

        private unsafe SecurityStatus DecryptMessage(void* buffer, ref int offset, ref int count)
        {
            var securityBuff = stackalloc SecurityBuffer[4];
            SecurityBufferDescriptor sdcInOut = new SecurityBufferDescriptor(4);
            securityBuff[0].size = count;
            securityBuff[0].tokenPointer = buffer;
            securityBuff[0].type = SecurityBufferType.Data;
            securityBuff[1].size = 0;
            securityBuff[1].tokenPointer = null;
            securityBuff[1].type = SecurityBufferType.Empty;
            securityBuff[2].size = 0;
            securityBuff[2].tokenPointer = null;
            securityBuff[2].type = SecurityBufferType.Empty;
            securityBuff[3].size = 0;
            securityBuff[3].tokenPointer = null;
            securityBuff[3].type = SecurityBufferType.Empty;

            sdcInOut.UnmanagedPointer = securityBuff;

            var errorCode = (SecurityStatus)InteropSspi.DecryptMessage(ref _contextPointer, sdcInOut, 0, null);

            if (errorCode == 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (securityBuff[i].type == SecurityBufferType.Data)
                    {
                        //we have found the data lets find the offset
                        offset = (int)((byte*)securityBuff[i].tokenPointer - (byte*)buffer);
                        if (offset > (count - 1))
                            throw new OverflowException();
                        count = securityBuff[i].size;
                        return errorCode;
                    }
                }
            }
            throw new NotImplementedException();
        }
    }
}
