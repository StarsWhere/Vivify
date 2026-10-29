Shader "Vivify/Checks/RefractOther"
{
    Properties { _Prefix("Prefix",Color)=(0,0,0,0) }
    SubShader
    {
        Tags { "Queue"="Transparent" }
        UsePass "Vivify/Checks/Refract/PREFIX"
        UsePass "Vivify/Checks/Refract/REFRACT"
    }
}
