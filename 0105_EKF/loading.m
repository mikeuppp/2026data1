
clc; clear;

load ax.mat
load ay.mat
load az.mat

load wx.mat
load wy.mat
load wz.mat

load X.mat
load Y.mat
load Z.mat

dt = 0.01;                          % your sample time
N  = length(ax);                    % number of samples
t  = (0:N-1)' * dt;                 % column vector time base

ax = timeseries(ax(:), t);
ay = timeseries(ay(:), t);
az = timeseries(az(:), t);

wx = timeseries(wx(:), t);
wy = timeseries(wy(:), t);
wz = timeseries(wz(:), t);

x  = timeseries(x(:),  t);
y  = timeseries(y(:),  t);
z  = timeseries(z(:),  t);

x.Data = double(x.Data);
y.Data = double(y.Data);
z.Data = double(z.Data);

% Replace NaN/Inf with previous value
x.Data = fillmissing(x.Data,'previous');
y.Data = fillmissing(y.Data,'previous');
z.Data = fillmissing(z.Data,'previous');

% If first sample is NaN, replace with zero
x.Data = fillmissing(x.Data,'constant',0);
y.Data = fillmissing(y.Data,'constant',0);
z.Data = fillmissing(z.Data,'constant',0);

