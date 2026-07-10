export HSA_ENABLE_DXG_DETECTION=1
if [ -d /opt/rocm-7.2.0/lib ]; then
  export LD_LIBRARY_PATH=/opt/rocm-7.2.0/lib:${LD_LIBRARY_PATH:-}
fi
if [ -d /opt/rocm-7.2.1/bin ]; then
  export PATH=/opt/rocm-7.2.1/bin:/opt/rocm/bin:${PATH:-}
elif [ -d /opt/rocm/bin ]; then
  export PATH=/opt/rocm/bin:${PATH:-}
fi
