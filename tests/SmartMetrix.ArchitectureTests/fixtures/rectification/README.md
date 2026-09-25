`numpy-maps.npz` is a small interoperability fixture produced with NumPy 2.2.6:

```python
y, x = numpy.indices((6, 8), dtype=numpy.float32)
numpy.savez_compressed('numpy-maps.npz', leftX=x + .25, leftY=y, rightX=x, rightY=y + .5)
```

It validates the .NET loader against actual NumPy output, independently of the test NPZ writer.
