﻿using System;
using System.Runtime.InteropServices;
using Channels.Networking.Windows.Tls.Internal;

namespace Channels.Networking.Windows.Tls
{
    internal unsafe class SecureClientContext:ISecureContext
    {
        private SecurityContext _securityContext;
        private SSPIHandle _contextPointer;
        private int _headerSize = 5; //5 is the minimum (1 for frame type, 2 for version, 2 for frame size)
        private int _trailerSize = 16;
        private int _maxDataSize = 16354;
        private bool _readyToSend;
        private ApplicationProtocols.ProtocolIds _negotiatedProtocol;
        public bool ReadyToSend => _readyToSend;
        public ApplicationProtocols.ProtocolIds NegotiatedProtocol => _negotiatedProtocol;
        public int TrailerSize => _trailerSize;
        public int HeaderSize => _headerSize;
        public SSPIHandle ContextHandle => _contextPointer;

        public SecureClientContext(SecurityContext securityContext)
        {
            _securityContext = securityContext;
        }

        public void Dispose()
        {
            if (_contextPointer.IsValid) { InteropSspi.DeleteSecurityContext(ref _contextPointer);}
        }
        
        public byte[] ProcessContextMessage(ReadableBuffer messageBuffer)
        {
            GCHandle handleForAllocation = default(GCHandle);
            try
            {
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
                    inputBuff[0].type = SecurityBufferType.Token;

                    if (messageBuffer.IsSingleSpan)
                    {
                        void* arrayPointer;
                        messageBuffer.First.TryGetPointer(out arrayPointer);
                        inputBuff[0].tokenPointer = arrayPointer;
                    }
                    else
                    {
                        if (messageBuffer.Length <= SecurityContext.MaxStackAllocSize)
                        {

                            byte* tempBuffer = stackalloc byte[messageBuffer.Length];
                            Span<byte> tmpSpan = new Span<byte>(tempBuffer, messageBuffer.Length);
                            messageBuffer.CopyTo(tmpSpan);
                            inputBuff[0].tokenPointer = tempBuffer;
                        }
                        else
                        {
                            //We have to allocate... sorry
                            byte[] tempBuffer = new byte[messageBuffer.Length];
                            Span<byte> tmpSpan = new Span<byte>(tempBuffer);
                            messageBuffer.CopyTo(tmpSpan);
                            handleForAllocation = GCHandle.Alloc(tempBuffer, GCHandleType.Pinned);
                            inputBuff[0].tokenPointer = (void*)handleForAllocation.AddrOfPinnedObject();
                        }
                    }

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

                        inputBuff[0].tokenPointer = (void*)_securityContext.AlpnSupportedProtocols;

                        inputBuff[0].type = SecurityBufferType.ApplicationProtocols;

                        input.UnmanagedPointer = inputBuff;
                        pointerToDescriptor = &input;
                    }
                }

                long timestamp = 0;
                SecurityStatus errorCode = (SecurityStatus)InteropSspi.InitializeSecurityContextW(ref handle, contextptr, _securityContext.HostName, SecurityContext.RequiredFlags | ContextFlags.InitManualCredValidation, 0, Endianness.Native, pointerToDescriptor, 0, newContextptr, output, ref unusedAttributes, out timestamp);

                _contextPointer = localhandle;

                if (errorCode == SecurityStatus.ContinueNeeded || errorCode == SecurityStatus.OK)
                {
                    byte[] outArray = null;
                    if (outputBuff[0].size > 0)
                    {
                        outArray = new byte[outputBuff[0].size];
                        Marshal.Copy((IntPtr)outputBuff[0].tokenPointer, outArray, 0, outputBuff[0].size);
                        InteropSspi.FreeContextBuffer((IntPtr)outputBuff[0].tokenPointer);
                    }
                    if (errorCode == SecurityStatus.OK)
                    {
                        ContextStreamSizes ss;
                        //We have a valid context so lets query it for info
                        InteropSspi.QueryContextAttributesW(ref _contextPointer, ContextAttribute.StreamSizes, out ss);
                        _headerSize = ss.header;
                        _trailerSize = ss.trailer;

                        if (_securityContext.LengthOfSupportedProtocols > 0)
                        {
                            _negotiatedProtocol = ApplicationProtocols.FindNegotiatedProtocol(_contextPointer);
                        }
                        _readyToSend = true;
                    }
                    return outArray;
                }

                throw new InvalidOperationException($"An error occured trying to negoiate a session {errorCode}");
            }
            finally
            {
                if(handleForAllocation.IsAllocated) { handleForAllocation.Free();      }
            }
        }
    }
}
