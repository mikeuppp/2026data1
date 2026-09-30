clc;
clear;

dt = 0.01;

P = 0;
F = eye(9);
F(1:3,4:6) = eye(3)*dt;

Q = diag([0.1*ones(1,3) 0.05*ones(1,3) 0.001*ones(1,3)]);
P = F*P*F' + Q;