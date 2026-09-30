function EKF_IMU_GPS_SF(block)
setup(block);
end

%% ==============================================================
function setup(block)

% ---------- Ports ----------
block.NumInputPorts  = 2;   % IMU and GPS
block.NumOutputPorts = 1;

% ---------- IMU input  ----------
block.InputPort(1).Dimensions  = 6;   % ax ay az wx wy wz
block.InputPort(1).DatatypeID = 0;
block.InputPort(1).Complexity = 'Real';
block.InputPort(1).DirectFeedthrough = true;

% ---------- GPS input ----------
block.InputPort(2).Dimensions  = 3;   % x y z
block.InputPort(2).DatatypeID = 0;
block.InputPort(2).Complexity = 'Real';
block.InputPort(2).DirectFeedthrough = true;

% ---------- Output (18 signals as before) ----------
block.OutputPort(1).Dimensions = 18;
block.OutputPort(1).DatatypeID = 0;
block.OutputPort(1).Complexity = 'Real';

% ---------- Sample time ----------
block.SampleTimes = [0 0];   % inherited

% ---------- EKF parameter ----------
block.NumDialogPrms = 1;     % dt

% ---------- Registration ----------
block.RegBlockMethod('PostPropagationSetup', @DoPostPropSetup);
block.RegBlockMethod('InitializeConditions', @InitConditions);
block.RegBlockMethod('Update',              @Update);
block.RegBlockMethod('Outputs',             @Outputs);

end

%% ==============================================================
function DoPostPropSetup(block)

block.NumDworks = 2;

% State x (9)
block.Dwork(1).Name            = 'x';
block.Dwork(1).Dimensions      = 9;
block.Dwork(1).DatatypeID      = 0;
block.Dwork(1).Complexity      = 'Real';
block.Dwork(1).UsedAsDiscState = true;

% Covariance P (9x9 flattened)
block.Dwork(2).Name            = 'P';
block.Dwork(2).Dimensions      = 81;
block.Dwork(2).DatatypeID      = 0;
block.Dwork(2).Complexity      = 'Real';
block.Dwork(2).UsedAsDiscState = true;

end

%% ==============================================================
function InitConditions(block)

block.Dwork(1).Data = zeros(9,1);
block.Dwork(2).Data = reshape(eye(9),81,1);

end

%% ==============================================================
function Update(block)

dt = block.DialogPrm(1).Data;

x = block.Dwork(1).Data;
P = reshape(block.Dwork(2).Data,9,9);

% -------- IMU input --------
imu = block.InputPort(1).Data;
acc  = imu(1:3);
gyro = imu(4:6);

% -------- GPS input --------
gps = block.InputPort(2).Data;

% -------- orientation --------
roll  = x(7); pitch = x(8); yaw = x(9);
Rbg = eul2rotm([yaw pitch roll]);

% -------- Prediction --------
acc_g = Rbg * acc;

x(1:3) = x(1:3) + x(4:6)*dt + 0.5*acc_g*dt^2;
x(4:6) = x(4:6) + acc_g*dt;
x(7:9) = x(7:9) + gyro*dt;

F = eye(9);
F(1:3,4:6) = eye(3)*dt;

Q = diag([0.1*ones(1,3) 0.05*ones(1,3) 0.001*ones(1,3)]);
P = F*P*F' + Q;

% -------- Update step (only if GPS valid) --------
gps_valid = ~any(isnan(gps));
if gps_valid
    H = [eye(3) zeros(3,6)];
    R = 4*eye(3);

    y = gps - H*x;
    S = H*P*H' + R;
    K = P*H'/S;

    x = x + K*y;
    P = (eye(9)-K*H)*P;
end

block.Dwork(1).Data = x;
block.Dwork(2).Data = reshape(P,81,1);

end

%% ==============================================================
function Outputs(block)

x = block.Dwork(1).Data;
P = reshape(block.Dwork(2).Data,9,9);

gps = block.InputPort(2).Data;
gps_valid = ~any(isnan(gps));

pos_err = zeros(3,1);
if gps_valid
    pos_err = x(1:3) - gps;
end

block.OutputPort(1).Data = ...
[ x
  pos_err
  abs(pos_err)
  norm(pos_err)
  trace(P(1:3,1:3))
  gps_valid ];

end
