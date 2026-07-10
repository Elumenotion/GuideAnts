export HSA_ENABLE_DXG_DETECTION=1
for _rocm_lib in /opt/rocm-*/lib /opt/rocm/lib; do
  if [ -d "$_rocm_lib" ]; then
    export LD_LIBRARY_PATH="$_rocm_lib:${LD_LIBRARY_PATH:-}"
    break
  fi
done
for _rocm_bin in /opt/rocm-*/bin /opt/rocm/bin; do
  if [ -d "$_rocm_bin" ]; then
    export PATH="$_rocm_bin:/opt/rocm/bin:${PATH:-}"
    break
  fi
done
unset _rocm_lib _rocm_bin
