clc; clear; close all;

%% Simulation Parameters
dt = 0.01;       % Time step (s)
T = 50;          % Total simulation time (s)
N = T/dt;        % Number of steps

%% Vehicle True Trajectory (3D motion example)
t = linspace(0,T,N);
true_pos = [50*sin(0.1*t); 50*cos(0.1*t); 0.5*t];   % x, y, z
true_vel = [diff([0 true_pos(1,:)])/dt; diff([0 true_pos(2,:)])/dt; diff([0 true_pos(3,:)])/dt];
true_vel(:,1) = true_vel(:,2); % fix first step
true_orient = [0.01*t; 0.02*t; 0.05*t]; % roll, pitch, yaw

%% IMU Simulation (accelerometer + gyro in body frame)
acc_noise_std = 0.2;   % m/s^2
gyro_noise_std = 0.01; % rad/s

acc_body = diff([zeros(3,1) true_vel],1,2)/dt + acc_noise_std*randn(3,N);
gyro_body = diff([zeros(3,1) true_orient],1,2)/dt + gyro_noise_std*randn(3,N);

%% GPS Simulation
gps_noise_std = 2.0; % meters
gps_pos = true_pos + gps_noise_std*randn(size(true_pos));
gps_pos(:,1000:1200) = NaN; % Simulate GPS outage

%% EKF Initialization
x = zeros(9,1);           % [x;y;z;vx;vy;vz;roll;pitch;yaw]
P = eye(9);               % Covariance
Q = diag([0.1*ones(1,3), 0.05*ones(1,3), 0.001*ones(1,3)]); % Process noise
R = gps_noise_std^2 * eye(3); % GPS measurement noise

F = eye(9);   % placeholder, will update in loop
H = [eye(3) zeros(3,6)];

ekf_pos = zeros(3,N);
ekf_orient = zeros(3,N);

%% EKF Loop
for k = 1:N
    % Prediction Step
    roll = x(7); pitch = x(8); yaw = x(9);
    Rb2g = eul2rotm([yaw, pitch, roll]); % body to global

    % Accelerations in global frame
    acc_global = Rb2g * acc_body(:,k);

    % State prediction
    x(1:3) = x(1:3) + x(4:6)*dt + 0.5*acc_global*dt^2;
    x(4:6) = x(4:6) + acc_global*dt;
    x(7:9) = x(7:9) + gyro_body(:,k)*dt;

    % Jacobian (approximate)
    F = eye(9);
    F(1:3,4:6) = eye(3)*dt;

    % Covariance prediction
    P = F*P*F' + Q;

    % Update Step (if GPS available)
    if ~any(isnan(gps_pos(:,k)))
        z = gps_pos(:,k);
        y = z - H*x;
        S = H*P*H' + R;
        K = P*H'/S;
        x = x + K*y;
        P = (eye(9)-K*H)*P;
    end

    % Store results
    ekf_pos(:,k) = x(1:3);
    ekf_orient(:,k) = x(7:9);
end

%% Plotting
figure;
subplot(3,1,1);
plot(true_pos(1,:),true_pos(2,:),'k--','LineWidth',1.5); hold on;
plot(gps_pos(1,:),gps_pos(2,:),'bo','MarkerSize',2);
plot(ekf_pos(1,:),ekf_pos(2,:),'r-','LineWidth',1.5);
xlabel('X [m]'); ylabel('Y [m]');
legend('True','GPS','EKF'); grid on; title('Position XY');

subplot(3,1,2);
plot(true_pos(3,:),'k--'); hold on;
plot(ekf_pos(3,:),'r-','LineWidth',1.5);
xlabel('Time step'); ylabel('Z [m]'); legend('True Z','EKF Z'); grid on;

subplot(3,1,3);
plot(true_orient','k--'); hold on;
plot(ekf_orient','r-','LineWidth',1.5);
xlabel('Time step'); ylabel('Orientation [rad]'); legend('Roll','Pitch','Yaw'); grid on;

%% Compute RMSE
pos_rmse = sqrt(mean((ekf_pos - true_pos).^2,2));
orient_rmse = sqrt(mean((ekf_orient - true_orient).^2,2));
disp('Position RMSE [m]:'); disp(pos_rmse');
disp('Orientation RMSE [rad]:'); disp(orient_rmse');

%% ===================== ERROR ANALYSIS =====================

% Signed position error
pos_error_signed = ekf_pos - true_pos;

% Absolute position error
pos_error_abs = abs(pos_error_signed);

% Euclidean (3D) position error
pos_error_euclid = sqrt(sum(pos_error_signed.^2, 1));

% Time-varying RMSE (sliding window)
window = 200; % samples
rmse_time = zeros(1,N);
for k = window:N
    rmse_time(k) = sqrt(mean(pos_error_euclid(k-window+1:k).^2));
end

% Final RMS metrics
rmse_xyz = sqrt(mean(pos_error_signed.^2,2));
rmse_3d  = sqrt(mean(pos_error_euclid.^2));

fprintf('\n===== POSITION ERROR METRICS =====\n');
fprintf('RMSE X [m]: %.3f\n', rmse_xyz(1));
fprintf('RMSE Y [m]: %.3f\n', rmse_xyz(2));
fprintf('RMSE Z [m]: %.3f\n', rmse_xyz(3));
fprintf('3D RMSE [m]: %.3f\n', rmse_3d);

figure;
subplot(3,1,1);
plot(pos_error_signed(1,:)); grid on;
ylabel('X Error [m]');
title('Signed Position Error');

subplot(3,1,2);
plot(pos_error_signed(2,:)); grid on;
ylabel('Y Error [m]');

subplot(3,1,3);
plot(pos_error_signed(3,:)); grid on;
ylabel('Z Error [m]');
xlabel('Time Step');

figure;
subplot(3,1,1);
plot(pos_error_abs(1,:)); grid on;
ylabel('|X| [m]');
title('Absolute Position Error');

subplot(3,1,2);
plot(pos_error_abs(2,:)); grid on;
ylabel('|Y| [m]');

subplot(3,1,3);
plot(pos_error_abs(3,:)); grid on;
ylabel('|Z| [m]');
xlabel('Time Step');

figure;
plot(pos_error_euclid,'LineWidth',1.5);
grid on;
xlabel('Time Step');
ylabel('3D Position Error [m]');
title('Euclidean Position Error');

figure;
plot(rmse_time,'LineWidth',1.5);
grid on;
xlabel('Time Step');
ylabel('RMSE [m]');
title('Sliding Window RMSE');

x = gps_pos(1, :); y = gps_pos(2, :); z = gps_pos(3, :);
ax = acc_body(1, :); ay = acc_body(2, :); az = acc_body(3, :);
wx = gyro_body(1, :); wy = gyro_body(2, :); wz = gyro_body(3, :);
save("X.mat", "x"); save("Y.mat", "y"); save("Z.mat", "z");
save("ax.mat", "ax"); save("ay.mat", "ay"); save("az.mat", "az");
save("wx.mat", "wx"); save("wy.mat", "wy"); save("wz.mat", "wz");