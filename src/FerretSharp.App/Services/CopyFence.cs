using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FerretSharp.App.Services;

/// <summary>
/// Waits until the GPU has done the copy of a WebView frame (ADR 0039). The composition control copies each frame with
/// Direct3D 11 into a shared texture, which WPF's render thread presents through its own Direct3D 9 device – without any
/// synchronization between the two. A copy not yet done when WPF presents shows the previous picture, until the next frame
/// (a caret blink, the next key): typed characters appeared 0.2–2 s late, in a large window about every fifth one. An event
/// query after the copy, waited for here on the UI thread, holds the commit back until the copy is in the texture.
/// </summary>
internal sealed class CopyFence
{
    private const int QueryEvent = 0;
    private const int False = 1;

    private object? _context;
    private ID3D11Asynchronous? _query;

    /// <summary>
    /// Waits for the copies queued on the control's context so far, at most <paramref name="limit"/>.
    /// </summary>
    /// <param name="device">The control's <c>ID3D11Device</c> (an RCW of its own interop interface).</param>
    /// <param name="context">The control's immediate context, from the same device.</param>
    /// <returns>How long it waited; null if the copy was still not done after the limit.</returns>
    public TimeSpan? Wait(object device, object context, TimeSpan limit)
    {
        if (!ReferenceEquals(context, _context))
        {
            // New devices after a rebuild: a query belongs to the device it was created on.
            Release();
            Marshal.ThrowExceptionForHR(((ID3D11Device)device).CreateQuery(new QueryDescription { Query = QueryEvent }, out var query));
            _query = query;
            _context = context;
        }

        var deviceContext = (ID3D11DeviceContext)context;
        deviceContext.End(_query!);
        var start = Stopwatch.GetTimestamp();
        while (true)
        {
            var hr = deviceContext.GetData(_query!, out _, sizeof(int), 0); // flags 0: flushes the context first
            if (hr != False)
            {
                Marshal.ThrowExceptionForHR(hr);
                return Stopwatch.GetElapsedTime(start);
            }

            if (Stopwatch.GetElapsedTime(start) > limit)
            {
                return null;
            }

            Thread.Yield();
        }
    }

    /// <summary>The query is ours; the device and context belong to the control and are never released here.</summary>
    public void Release()
    {
        if (_query is not null)
        {
            Marshal.ReleaseComObject(_query);
        }

        _query = null;
        _context = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryDescription
    {
        public int Query;
        public int MiscFlags;
    }

    /// <summary><c>ID3D11Device</c> up to <c>CreateQuery</c>; the slots before it are never called.</summary>
    [ComImport]
    [Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Device
    {
        void CreateBuffer();
        void CreateTexture1D();
        void CreateTexture2D();
        void CreateTexture3D();
        void CreateShaderResourceView();
        void CreateUnorderedAccessView();
        void CreateRenderTargetView();
        void CreateDepthStencilView();
        void CreateInputLayout();
        void CreateVertexShader();
        void CreateGeometryShader();
        void CreateGeometryShaderWithStreamOutput();
        void CreatePixelShader();
        void CreateHullShader();
        void CreateDomainShader();
        void CreateComputeShader();
        void CreateClassLinkage();
        void CreateBlendState();
        void CreateDepthStencilState();
        void CreateRasterizerState();
        void CreateSamplerState();

        [PreserveSig]
        int CreateQuery(in QueryDescription description, out ID3D11Asynchronous query);
    }

    /// <summary><c>ID3D11DeviceContext</c> up to <c>GetData</c> (after the four methods of <c>ID3D11DeviceChild</c>).</summary>
    [ComImport]
    [Guid("c0bfa96c-e089-44fb-8eaf-26f8796190da")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11DeviceContext
    {
        void GetDevice();
        void GetPrivateData();
        void SetPrivateData();
        void SetPrivateDataInterface();
        void VSSetConstantBuffers();
        void PSSetShaderResources();
        void PSSetShader();
        void PSSetSamplers();
        void VSSetShader();
        void DrawIndexed();
        void Draw();
        void Map();
        void Unmap();
        void PSSetConstantBuffers();
        void IASetInputLayout();
        void IASetVertexBuffers();
        void IASetIndexBuffer();
        void DrawIndexedInstanced();
        void DrawInstanced();
        void GSSetConstantBuffers();
        void GSSetShader();
        void IASetPrimitiveTopology();
        void VSSetShaderResources();
        void VSSetSamplers();
        void Begin();

        [PreserveSig]
        void End(ID3D11Asynchronous async);

        [PreserveSig]
        int GetData(ID3D11Asynchronous async, out int data, int dataSize, int flags);
    }

    /// <summary>The query, passed as the <c>ID3D11Asynchronous</c> it derives from; none of its own methods are called.</summary>
    [ComImport]
    [Guid("4b35d0cd-1e15-4258-9c98-1b1333f6dd3b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3D11Asynchronous;
}
