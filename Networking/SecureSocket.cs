﻿using System.Net.Sockets;
using System.Text;
using System.Numerics;
using System.Security.Cryptography;
using System.Buffers;
using Cryptography.Generic;

namespace Cryptography.Networking
{
    public class SignatureInvalidExeption : Exception
    {
        public SignatureInvalidExeption() : base("The signature was invalid! Possible man in the middle attack") { }
    }

    public class SecureSocket : IDisposable
    {
        private Socket socket;
        private MemoryPool<byte> mem_pool;

        private const int pub_key_bytes = 32;
        private Curves default_curve = Curves.microsoft_160;

        private Encryption encryption = new Encryption();

        public SecureSocket(Socket socket)
        {
            this.socket = socket;
            mem_pool = MemoryPool<byte>.Shared;
        }

        public void Dispose()
        {
            close();
            mem_pool.Dispose();
        }

        public void close()
        {
            socket.Close();
        }
        ~SecureSocket()
        {
            Dispose();
        }
        private Span<byte> generatePayload(Coordinate public_key)
        {
            Span<byte> x = public_key.x.ToByteArray();
            Span<byte> y = public_key.y.ToByteArray();

            byte[] x_padded = new byte[32];
            x.CopyTo(x_padded);

            byte[] y_padded = new byte[32];
            y.CopyTo(y_padded);

            Span<byte> payload = x_padded.Concat(y_padded).ToArray();
            return payload;
        }

        private Coordinate decodePayload(Span<byte> payload)
        {
            BigInteger x = new BigInteger(payload.Slice(0, pub_key_bytes));
            BigInteger y = new BigInteger(payload.Slice(pub_key_bytes, pub_key_bytes));

            Coordinate public_key = new Coordinate(x, y);
            return public_key;
        }

        private string sendHandshake()
        {
            KeyPair key_pair = new KeyPair(default_curve);
            ECC ecc = new ECC(default_curve);

            Span<byte> payload = generatePayload(key_pair.public_component);
            sendRaw(payload);

            Coordinate public_key = decodePayload(recvRaw(pub_key_bytes * 2));
            string key = ecc.ECDH(key_pair.private_component, public_key);

            return key;
        }

        private string recvHandshake()
        {
            KeyPair key_pair = new KeyPair(default_curve);
            ECC ecc = new ECC(default_curve);

            Coordinate public_key = decodePayload(recvRaw(pub_key_bytes * 2));
            string key = ecc.ECDH(key_pair.private_component, public_key);

            Span<byte> payload = generatePayload(key_pair.public_component);
            sendRaw(payload);

            return key;
        }

        private string sendHandshakeSigned(BigInteger signature_private_key)
        {
            KeyPair key_pair = new KeyPair(default_curve);
            ECC ecc = new ECC(default_curve);

            Span<byte> payload = generatePayload(key_pair.public_component);
            sendRaw(payload);

            string signature = ecc.generateDSAsignature(Encoding.UTF8.GetString(payload), signature_private_key).signature;
            sendArbitrary(Encoding.UTF8.GetBytes(signature));

            Coordinate public_key = decodePayload(recvRaw(pub_key_bytes * 2));
            string key = ecc.ECDH(key_pair.private_component, public_key);

            return key;
        }

        private string recvHandshakeSigned(string signature_public_key)
        {
            KeyPair key_pair = new KeyPair(default_curve);
            ECC ecc = new ECC(default_curve);

            Span<byte> payload = recvRaw(pub_key_bytes * 2);
            
            string signature = Encoding.UTF8.GetString(recvArbitrary());
            if (!ecc.verifyDSAsignature(Encoding.UTF8.GetString(payload), signature, signature_public_key))
            {
                throw new SignatureInvalidExeption();
            }

            Coordinate public_key = decodePayload(payload);
            string key = ecc.ECDH(key_pair.private_component, public_key);

            payload = generatePayload(key_pair.public_component);
            sendRaw(payload);

            return key;
        }

        private int sendRaw(Span<byte> data)
        {
            IAsyncResult result = socket.BeginSend(data.ToArray(), 0, data.Length, SocketFlags.None, null, null);
            result.AsyncWaitHandle.WaitOne(socket.SendTimeout, true);
            
            if (!result.IsCompleted)
            {
                throw new TimeoutException("Connection timed out when trying to send data");
            }

            return socket.EndSend(result);
        }

        private Span<byte> recvRaw(int buffsize)
        {
            Span<byte> data_recieved;
            using (IMemoryOwner<byte> buffer = mem_pool.Rent(buffsize))
            {
                int bytes_received = socket.Receive(buffer.Memory.Span);

                data_recieved = buffer.Memory.Span.Slice(0, bytes_received);
            }

            return data_recieved;
        }

        public void sendArbitrary(Span<byte> data)
        {
            using (BinaryWriter stream = new BinaryWriter(new NetworkStream(socket)))
            {
                stream.Write(data.Length);
                stream.Write(data);
            }
        }

        public Span<byte> recvArbitrary()
        {
            using NetworkStream ns = new NetworkStream(socket);

            using (BinaryReader stream = new BinaryReader(ns))
            {
                int content_length = stream.ReadInt32();
                return recvRaw(content_length);
            }
        }

        public void secureSend(Span<byte> data)
        {
            encryption.RandomizeInitializationVector();

            string encryption_key = sendHandshake();
            Span<byte> encrypted_data = encryption.AESencrypt(data, encryption_key);

            sendArbitrary(encryption.initialization_vector);
            sendArbitrary(encrypted_data);
        }

        public Span<byte> secureRecv()
        {
            string encryption_key = recvHandshake();
            encryption.initialization_vector = recvArbitrary().ToArray();
            Span<byte> encrypted_data = recvArbitrary();

            Span<byte> decrypted_data = encryption.AESdecrypt(encrypted_data, encryption_key);
            return decrypted_data;
        }

        public void secureSendSigned(BigInteger private_key, Span<byte> data)
        {
            encryption.RandomizeInitializationVector();

            string encryption_key = sendHandshakeSigned(private_key);
            Span<byte> encrypted_data = encryption.AESencrypt(data, encryption_key);

            sendArbitrary(encryption.initialization_vector);
            sendArbitrary(encrypted_data);
        }

        public Span<byte> secureRecvSigned(string public_key)
        {
            string encryption_key = recvHandshakeSigned(public_key);
            encryption.initialization_vector = recvArbitrary().ToArray();
            Span<byte> encrypted_data = recvArbitrary();

            Span<byte> decrypted_data = encryption.AESdecrypt(encrypted_data, encryption_key);
            return decrypted_data;
        }
    }
}
