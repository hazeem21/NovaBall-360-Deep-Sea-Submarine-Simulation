/*
 * NOVA Controller for Unity
 * Copyright (C) 2020-2021, Eight360 Limited
 * All Rights Reserved
 *
 * THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
 * WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
 * OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 *
 * 2020-11-11: DA - Tidy up for distribution.
 *
 */

using System;
using System.Text;
using System.Net;
using System.Net.Sockets;
using UnityEngine;

namespace Eight360.SDK
{
    public class NovaController : MonoBehaviour
    {
        public enum Mode
        {
            MotionCueing,
            Quaternion,
            CuedQuaternion,
            Legacy
        };

        [Tooltip("Name or IPv4 address of NOVA Control System")]
        public string novaIpAddress = "localhost";

        [Tooltip("IPv4 port number of NOVA Control System")]
        public int novaPort = 28360;

        [Tooltip("Control message format")]
        public Mode controlMode = Mode.Quaternion;

        [Tooltip("RigidBody for control")]
        public Rigidbody rigidBody;

        [Tooltip("Motion Cueing Factor, 1.0 realistic, 0.0 off")]
        public float motionCueingFactor = 1.0f;

        [Header("Filter Weights")]
        [Tooltip("acceleration filter parameter 0 unfiltered 1 static")]
        public float aAccelFilt = 0.95f;

        [Tooltip("Quaternion filter parameter 0 unfiltered 1 static")]
        public float aQuatFilt = 0.92f;



        [HideInInspector]
        public bool active = false;

        private Vector3 lastVelocity;

        private Vector3 smoothAccel;
        private Quaternion smoothQuat;
        private IPEndPoint remoteEndPoint;
        private UdpClient client;
        private int sequenceNumber;

        private bool allowMotionCueing = true;

        public void Start()
        {
            // If rigidbody is not initially defined try get it from the parent, else use given RB.
            if (!rigidBody)
            {
                rigidBody = GetComponent<Rigidbody>();
            }

            if (!rigidBody)
            {
                //Debug.LogWarning("Component " + this.name + " has no Rigidbody component");
                active = false;
            }
            else
            {
                TryActivate();
            }

            StartCoroutine(PostFixedUpdateCoroutine());
        }

        public void ControlStateQuat()
        {
            controlMode = Mode.Quaternion;
            //Debug.Log("Switching to Quat");

        }

        public void ControlStateCued()
        {
            if (allowMotionCueing)
            {
                controlMode = Mode.CuedQuaternion;
                //Debug.Log("Switched to CuedQuat");
            }
        }

        private void TryActivate()
        {
            remoteEndPoint = GetRemoteEndpoint(novaIpAddress, novaPort);
            Debug.Log("Sending NOVA Control messages to " + remoteEndPoint.Address + " : " + remoteEndPoint.Port);
            client = new UdpClient(remoteEndPoint.AddressFamily);
            sequenceNumber = 0;
            active = true;
        }

        public void SetRigidBody(Rigidbody rb)
        {
            rigidBody = rb;
            TryActivate();
        }

        public Mode GetControlMode()
        {
            return controlMode;
        }

        public void SetIP(string address)
        {
            novaIpAddress = address;
        }

        public void SetPort(int port)
        {
            novaPort = port;
        }

        private IPEndPoint GetRemoteEndpoint(string ipAddress, int port)
        {
            var resolvedIP = Dns.GetHostEntry(ipAddress).AddressList[0];
            return new IPEndPoint(resolvedIP, port);
        }

        private System.Collections.IEnumerator PostFixedUpdateCoroutine()
        {
            while (true)
            {
                yield return new WaitForFixedUpdate();
                PostFixedUpdate();
            }
        }

        private void PostFixedUpdate()
        {
            if (!rigidBody || !active) return;

            if (!allowMotionCueing && (controlMode == Mode.MotionCueing || controlMode == Mode.CuedQuaternion))
            {
                // disallow motion cueing
                Debug.LogWarning("Motion Cueing not permitted");
                controlMode = Mode.Quaternion;
            }

            var velocity = rigidBody.linearVelocity;
            var rotation = rigidBody.rotation;
            var acceleration = (velocity - lastVelocity) / Time.fixedDeltaTime;
            lastVelocity = velocity;
            ++sequenceNumber;

            switch (controlMode)
            {
                case Mode.MotionCueing:
                    SendAsMotion(sequenceNumber, rotation, acceleration);
                    break;

                case Mode.CuedQuaternion:
                    SendAsCuedQuaternion(sequenceNumber, rotation, acceleration);
                    break;

                case Mode.Quaternion:
                    SendAsQuaternion(sequenceNumber, rotation);
                    break;

                case Mode.Legacy:
                    SendAsLegacy(sequenceNumber, rotation, acceleration);
                    break;

                default:
                    Debug.LogWarning("Invalid Control Mode " + controlMode.ToString());
                    active = false;
                    break;
            }
        }

        private void SendAsMotion(int sequenceNumber, Quaternion rotation, Vector3 acceleration)
        {
            // Convert from Unity's left-handed Y-up, to NOVA's right-handed Z-up:
            Quaternion rhsOrientation = new Quaternion(-rotation.z, rotation.x, -rotation.y, rotation.w);
            Vector3 intrinsicAcceleration = Quaternion.Inverse(rotation) * acceleration;
            smoothAccel = aAccelFilt * smoothAccel + (1 - aAccelFilt) * intrinsicAcceleration;

            var msg = string.Format("{0},{1},{2},{3},{4},{5},{6},{7}\n",
                 sequenceNumber,
                 rhsOrientation.w,
                 rhsOrientation.x,
                 rhsOrientation.y,
                 rhsOrientation.z,
                 -smoothAccel.z, // Heave
                 smoothAccel.x, // Surge
                 -smoothAccel.y); // Sway

            SendControlMessage(msg);

            //Debug.Log("accel: " + smoothAccel.x + " : " + smoothAccel.y + " : " + smoothAccel.z);
        }

        private void SendAsQuaternion(int sequenceNumber, Quaternion unityRotation)
        {
            // Convert from Unity's left-handed Y-up, to NOVA's right-handed Z-up:
            Quaternion rhsOrientation = new Quaternion(-unityRotation.z, unityRotation.x, -unityRotation.y, unityRotation.w);

            // Unity Quaternion order is XYZW, but NOVA Control expects WXYZ
            var msg = string.Format("{0},{1},{2},{3},{4}\n", sequenceNumber,
                rhsOrientation.w, rhsOrientation.x, rhsOrientation.y, rhsOrientation.z);

            SendControlMessage(msg);
            Vector3 e = rhsOrientation.eulerAngles;
            //Debug.Log("quat: " + e.x + " : " + e.y + " : " + e.z);
        }

        private void SendAsCuedQuaternion(int sequenceNumber, Quaternion rotation, Vector3 acceleration)
        {
            // Convert from Unity's left-handed Y-up, to NOVA's right-handed Z-up:
            Vector3 gravity = new Vector3(0.0f, 9.81f, 0.0f);
            //compute the vehicle intrinsic force vector;
            acceleration = acceleration * motionCueingFactor;
            smoothAccel = aAccelFilt * smoothAccel + (1 - aAccelFilt) * (Quaternion.Inverse(rotation) * acceleration + gravity);
            //not perfect will break down at high accelerations
            double roll = 180.0f / Math.PI * Math.Atan2(smoothAccel[0], smoothAccel[1]);
            double pitch = -180.0f / Math.PI * Math.Atan2(smoothAccel[2], smoothAccel[1]);
            Quaternion Augmentedrotation = rotation * Quaternion.Euler((float)pitch, (float)0.0, (float)roll);
            smoothQuat = Quaternion.Slerp(smoothQuat, Augmentedrotation, aQuatFilt);
            Quaternion rhsOrientation = new Quaternion(-smoothQuat.z, smoothQuat.x, -smoothQuat.y, smoothQuat.w);


            // Unity Quaternion order is XYZW, but NOVA Control expects WXYZ
            var msg = string.Format("{0},{1},{2},{3},{4}\n", sequenceNumber,
                rhsOrientation.w, rhsOrientation.x, rhsOrientation.y, rhsOrientation.z);

            SendControlMessage(msg);
            Vector3 e = rhsOrientation.eulerAngles;
            //Debug.Log("cuedQuat: " + e.x + " : " + e.y + " : " + e.z);
        }

        private void SendAsLegacy(int sequenceNumber, Quaternion rotation, Vector3 acceleration)
        {
            var msg = string.Format("{0},{1},{2},{3},{4},{5},{6}",
                sequenceNumber,             // Segment ID
                rotation.eulerAngles.x,     // Pitch
                rotation.eulerAngles.y,     // Yaw
                rotation.eulerAngles.z,     // Roll
                acceleration.z,             // Heave
                acceleration.x,             // Surge
                acceleration.y              // Sway
            );

            SendControlMessage(msg);
            Debug.Log("Legacy-accel: " + acceleration.x + " : " + acceleration.y + " : " + acceleration.z);
        }

        private void SendControlMessage(string message)
        {
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(message);
                client.Send(dgram: data, bytes: data.Length, endPoint: remoteEndPoint);
            }
            catch (Exception err)
            {
                Debug.LogError(err.ToString());
            }
        }

        public void AllowMotionCueing(bool enable)
        {
            allowMotionCueing = enable;
        }
    }
}
