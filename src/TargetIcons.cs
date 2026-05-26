using MelonLoader;
using System;
using System.IO;
using System.IO.Compression;
using TMPro;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.TextCore;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private static bool targetIconsInitialized = false;
        private static TMP_SpriteAsset targetIconsSpriteAsset;

        private static readonly string[] iconNames = new string[]
        {
            "standard",
            "horizontal",
            "vertical",
            "chain",
            "chainstart",
            "hold",
            "melee",
            "mine"
        };

        // Zlib-compressed raw RGBA pixel data (64x64, bottom-up), base64 encoded
        private static readonly string[] iconBase64 = new string[]
        {
            // standard
            "eNrNW3lwDXkel0MiiGNnEEVcM664Mq64khgURtmxbqJsEUkhhVWGrOwK5dit4g/K/IO47WbL2NrBzBA1JWUpRzk2roSNa90x7jCOpI/ffj9d/X31e51+yevOE/lV/ar7ve7+9e97n12jRkBHEM0QmqHWCz169KgZERHRPDIyMq527doj6tSpk0THZJrT6HwiHYfS/ILO" +
            "G/tYG2sG16ieg+GWR+1atWol0PxTaGjoPvp9heZLmjpN4WMqNJ8GBQX9h575W3h4eBrho6u5Po9gm3d9zOG1F9rzoLCwsCw6vWOFr3HjxiI2NlYMHjxYHzNmjJaUlKRNnjxZGzVqlJaYmKh37NhRNGjQoAxegoOD82ndv9SsWbNLBTivyhEs0aUW7W1GSEjIGXnfnTt3" +
            "1tPS0pTs7Gz10qVLWnFxsS7KGZqm6U+ePNHPnDmjbdq0SZ06darSqlUrGRcq8cVP9J7hFvwHVTHsHvkm2vye9lPAe2zdurW+ePFi5ezZswSO5gtOoaqq1/R176+//ioOHTqkTZs2TWnYsKEHF4SHXJqJvvjwA8o566Bu2APvh/ha3bFjh/b69Wuv/SuK4oFP18slv3Ed" +
            "E/fzc/K4f/++vmrVKrVp06aahAfI2qdWunwgfmeaz6f5Fu9v3ry5mpWVpZWWlnrB7A+8/gzGh4yLx48fi4yMDJV0q6FLif/+R8cRNnIZaB0XSfjew7hPTU1VsRce2GMgYC4PF8Atj/Pnz4uEhATF1JGYyyQ+DQow7C0Iz+fwLpJDZffu3bpM76ocMh7AZ0uXLoU8aCYv" +
            "7JLkIDhAsLclut/E+jExMaUFBQUeuD8kvSsasox9//33OvlWpaZO+Ak2yaKv3Mp7K1rvNtaNj49XyD59FJr7wwunT58WUVFRpaY8HJR8xiAXeh6zIfHTZYaddbtVL1eHwfqX/Az4WaWSLLBdCHIAe4ip54FD0bVrV+XFixfVFnarHiL/SdStW7fU1ImZDm2jATvhbjn5" +
            "4aJRo0alt27dCjjsbOt5BpoP9u7dC9uoEBywVUP99JH4+gB6zniefC89EPIOXcU+jR28FV13gwPTLuikv+7R8TemLgiuwLerxf5senq6WlnY2de1jvfv34tXr14J6BS79cvzif3ViXi+f//+qmkTtlTAA8b/dN8i8H2HDh2Ud+/euaYH+2087t69K8g/FikpKaJv376i" +
            "TZs2okmTJoJ8WdG+fXvx5Zdfivnz54v9+/eLly9feuHBzeDn8vPzdfITVegCgq2vDxywjWhE9z2DL4GYw+375WdOnTolKM4V9erVE+XE/V4zOjpaLFq0SNy5c8cLn271IcVjqmkP/u3DLzJ0I8WwK3Hf0KFDlcrCDj8BtAYvMVzQRUQD4wh64Bomzvkazvl+xHurV6/2" +
            "wO5UHvAcnnn+/Dl4TcO7wsPDh1h4gO1iA3r3L9AXx44dc0V7xvfx48cN/gYMeCdgk/HAMPOUr/F14IJ/DxkyRDx8+FBUZk8rV640eIBofMjCA6Fm3iYF10k2VTe45n398MMPguSNY1MvuIAH0x7ZTiv9gQfar3H++eefixs3bjjmA/aRi4qKRP369Q2bFhYWFiPhINi0" +
            "98dA++3btzvW+Qz7kSNHBK3tgdVKb/4NmFq0aGHkwsi3MnSgFU9WvDAOHj165OFrp/ubPn26ESsSrf9qof1ndChF7g35Jyf6hvF77949+Epl9i/DDb2/ceNGUVhYKN68eeNZAzYwLy9PrFixAvkjL7xZcQA7wTbV3z3yvbm5uZq5p6uyTxwRETET/48ZM8ax3mM6jBw5" +
            "sgzPM+zAy65du/xar7i4WGRmZnqetcPBmjVrHOkCxtPbt2+Rr4EM6GZe2RjEs99h3aysLMWaY/BX5n3B3qlTJ3H9+nUvH0+OW9n/5Ws8Dhw4AB/eYyNkOaI41+A3J3LAe50yZYohA0TzOZyypnWvYV3kaJ3oF957XFycR88z7PhNuDb2Kfuk/qzJ9+bk5HhsI+OAcbxw" +
            "4UJHcTjft2XLFgN+onm2kdCKjGxHh3fNmjWDTPot+4xP+DdWPmX4f/75Z9e5gpKSEuO4ZMkSL53Ca1Ocb8iKv/tlmiK3bu73PHQ/8cFv8XvgwIGO9B7D9M0333jRhfdJuqRSeRLmbchsy5YtvWSKj5A7f/UAwwX9TnYQzz9FrY3g/wPWSk5OdmT3mPd79epVhj44Hj16" +
            "tEwM4NZ/Jd/FC8c4ggcQL/i7Z9mHjImJMWptqDeS7VuNNYnPVKdrPXv2THBNQrbxqNkgvnPrt1vt1oULF7z8RMY1bKETfcX3JSQkGHlzgn8Y6YHNOF+7dq3f8PM6ly9f9tLNvK+vv/46IPkSxh1qQOwjsc+M83bt2nn260Rn0f4MHUD8P4ng/zvON2zY4Df8vM7Jkye9" +
            "dB/z5+zZswOeI+3WrZuX/sM5cMI5SSfwT5o0STPpP920A/DLHMOPOEeWeYZ/zpw5AYe/e/fuZeBH/gA5FKfwo+Zswp9M8G/D+fr16x3zP+ovdn76hAkTAsr/sIVcB5b5HzGmEz3D+xk9ejTz/xTSf2txvnz5csfwIyalNcrov86dOwek/sfvQdzHcZWsZ/r06eNIx/J6" +
            "gwYNYv03kmLVdJynpaW5ivtMW+KVzwAf5OfnB8z+kV9ua//IZjuWM+wJcSdyXOjFIfpNwJpfffWVI9+X30kxpVe+go9ObHNF/Gr1MfiIfKJTm40eDPSh0PPFZLujiQdi4Qsgtma/24kscewjx2uYkAuOe9zwAO8lOzvbC2bWfVj/wYMHjv1f4kvN3Ot/kfL75JNPIul4" +
            "D/JFcqb7ywPs/8E/hW6S5Z/32r9/f0+N1AkOGPabN28K2p+XzmP+mjhxoiPcMo/s3r2b8+H7PMnP0NAc/Ldnzx5HOoDv+/bbb8vEv4wD2AK+r7y+CLnvAwO5X/RDWXmLY8Fz5865gp9ssxH/kd77M8NP5xn4LyUlxVH+g2MU1AlYD8q5Hz5PTEwUV65cKSM/2JNdn8vB" +
            "gweN/Jg1f8T4Zb3nVK5wP+FUM+GP9zTq1a7dAzmR6OhoHfzsxKbIvpBZYyiT88YRuYwFCxYYvrydfOG9hw8fFmPHjrXNnfE6wAviDqzhNPdB/opm2qkHAFvKf4fS/0bNKycnR3Mqr3wv+VBe9skODvyPnBDgJJsrZs6caeTOrHk/K+z4D3ll5Buc0p55PyMjg3MfW6X8" +
            "Z6iZA8uUc4BO89/8jmXLlpXxU+x+2027e5jnkTN2Eu9b9TTyrcQ7zPuJUg2E6wAt6f1vyR/UCwsLdac5ZplnwAcMh11OX673yPUgX7UCxDm5ubmu/Am+f+fOndD7Oq153qZnMsTkgZ1u9KCdLCD/wTEbywDjwq7ew3ixys6oUaPE7du3XcHONIQ9Jf2smvWU6Tb9EEb9" +
            "E3UR2kMpHbWCggK9sjiAXVi3bp1o27atT3634oLngAEDxL59+ypVB2Z8bd26VTNpf13qiwqyq38Tfjbh/cOGDatU7V/eL3Q7YJkxY4YRG8EWWPqbjTxS7969jbwubInst7npA+DnYCtIfhST9pPL6QHgGngU4cmogaOftbI5TCvd8B/iRsTOJ06cMHIoiJWePn1a4bNu" +
            "aE/xCde+j/rRA8N9T8mgSf369RU3NUdfeKgIHq69VDZvwLCTP6ubPePv6djJSQ8QPfMv4K1nz55GH4hbPvRlj+Qe8ED1Cstyd/XqVYN+Zuww30GPOPcBNTT1BfJFqpyPra6D6QNZQv+OqVv+6aI3XO5xfwXdOXfuXK0644Dpjnxgv379FFPm81DictkDyvgaRjjAevq8" +
            "efM0K66rw2B5R58L94ObPfHNK9kHzTj4HeHA6ClNSkpSOef4sXuA5Vo19HRsbKxi+pyAvW2AvgthHAyndYuxflxcnIIehsrY50DxO9fKo6KimO6X0LMd4G9iGAdfEA6u8TcA27Zt0+X9VAUe5PcgpklPT/f0/tPeDph9nh/ieyDGwaf0nu/Yd6PYVcnLy/PCQ6B1JPO5" +
            "vOb+/fv1Ll26KKYfDd92uaTjPtR3kiHSN0D43u0XM54Ws2bNUtFraaWVG/su94NYfSGKA/URI0YoUpwIHZ/4Ab578ecbsGbkU2+gYwnjYfz48cqPP/6ooWZpZ58552U3fclQUVGRTjGMGh8fr0oxw2Piw3Qznqmq799seYFwAD9hO52+5v0hJ5yamorvhNRr167pJSUl" +
            "fjMA+lAuXryobd68WR03bpyCHLAUM943+1SbVvG3fxV+9xseHt6GeGAJ0eaSHN8hd4P6wvDhwzXUmVasWKGi3piVlaWi7orae2ZmpooeDHwHi74h63fBhN8jtH6qpN8+1refvvxFmQZByKmitmbq5Lvmt83+9kDj28Kr9Ow/0JdKOG1nw3vVAW47PJTxs1FjIXx0j4iI" +
            "GE84+SPNtchBoveA5i58L03XVqMXB/1IdevW7RATExNmw2uhgYb7/66kaZs=",

            // horizontal
            "eNrtW2tMXEUUdnd5+IhVE6NCAi1to0BBsMW0koBgtFis5UcbUmNKGuJP+4g/FKNp1KrVxrRCoG0stFDTQmqwECXSoEZqjAYfiCRN/FFTRdPwqBAeBtjdO54zmXMzd7ivhd2y2+xJvszcubNnzjlzZubOzNlbbolTnOIUpzjFKaLkBXiklMq8ynuPVBar5HNR5zYpn2xRxyPxi2abeJRUzj8EeBpQCXgb0ALoBHwG+BbwDeBTQBugAfAqoBRQALjHhK9P" +
            "8pPl1tmr+Dfp+zzgY8BXgBkAWySGAOcABwCPAe5W7OGNgr72Crl2AtqF3LLOAZH6BYKiLCDy6jPWmVd+ixgFXAG8C8hSdE+4gXOYTA8DXgP8ZtJ3KLsmdNJC6HNNSoOSTeQ6I2L8lAAecJAxEpQCeB0wAZgSMgUl2OlEqaa81yx+w2z4/wXoATwTQRuo80w1oFeRze9Sp8XAynZ+xTbvA7Jt5F6KvyOv2wFHALNKu+HS060dmDI+5LKvATsc+i9U3XFt" +
            "TgR8bjK+nWSMtB3I9rIsY4BXwtTv2OeFkr9rFn7OogABRZb3lqj7rYBiwHcK/2jR18z+6nj4YAk2yJH63R+FeltBXW/fWsS6cAfgCwu/0mLMBth3e1zYQC7/MMxr2HLZgPK/A4ps1gO5rMpkfo1VO8g2OOdiLbwX0HUT9L3VOr3TYn9O+4cXHNZ3FuNjoR+QYdH3qdJap/uNx+NhVnl6lsvNYPc7s7wT7H6n5DXlOxH3kI8r8x2luL++6vV6zb65XQN/" +
            "71aPUHVd4jcC7RdaJH8n3ZPE2Yy+1mdmZrKsrCy2bt06lpGRwdasWcPy8vJ4is/Z2dkM62B+7dq1bMOGDSw/P39BH2B5bm4ur7d69WrOA+vjM/JHEA98h3UQyAvbIJsSv5KSEgMPlAHrmcmIsm/atEldt3EMPAhIFLpXAD4BDEs+w3p7e7WpqSk2NjbGiK5du6bnr1+/ziYnJ3l+fn6eTU9Ps6NHjxr0r6ioYMFgkI2Ojhp4aJrG8+Pj4xxIWCbzHx4e" +
            "Zl1dXWzFihW67mirmZkZzs/v9/N6KAPKYiajaFerqqriY5p0AwwCPgJsFHt4wx57/fr1bGJiQueD+qEeSJjis/yOnisrKzkfn8/H0/379+v1UN5AIGDJg/hjHdJtcHCQJSYm6vqnpKSwvr4+Rx70juxcU1NDcgWUeX1EpHjepJF90Idk3sTH7Bnz9Iy+Ket//vx5XScnHuo70iUnJ8cwBsAvF7RrxYPsfeHCBdkvg8p8oK/z5LebN2/mv5ubm9PbIf7ysypD" +
            "aWmppaxqasWH8qR/WVkZ55WQkMDTjo4OvY/NeMntUJ1Lly4tmEfszpyqq6sXtGFFZGdM09LSdDsnJyezy5cvu+ajEvnftm3bDPo3NjbqPmXFk8qoDo4jlMdmTTOU7d69OyT9abyuWrVKtzOOW2zXSVYrot+Ul5cbxtSpU6dC1r+/v58lJSW5WWu5HYqLiw3+b+e7Mm3ZssXgZ52dnYv2f5K9qKiI86J58OLFi679n1JcR6TxrX7bLDjXWLlypb62ue0rbGvr" +
            "1q0GXz1+/Ljuy6H0PdUdGRlhqampXG60KfIdGBgw6O/Gh06ePMkczszkM44g+vHQ0JDBB7A99HNMqX9I79nZWV533759Bv3RH9COVM+Oh/yOxlN3d7fBP9PT0w1jyo4HjUvM79q1i/wyoOwJ5wA/Av6Qvl/5+/b2dvcdJqinp8fw/YPfZosltOnhw4cN38A0LkOlwsJCZnJ3gHudl8X9CZ5xNoq2+Pq4fft21tzczFFXV8caGhrY2bNneYrPp0+f5nMx5tHP" +
            "W1tb2aFDh3RZaQ7Yu3cvO3PmDK9XX1/PeRw7dow/NzU1cWAey/Ad1sE2Tpw4wQoKCgx7Cvwua2trYy0tLbzN2tpaLgPKYiYjyK4dPHhQ3QPhfWu5dJco7/1/FX236PO+cO19aL6T+dEasMj9D433GoszsGRx7hWkNmnOkfPUr5gnebBMlk3dn9I7zKs8VP70O1VX2Q4yD6xnJSO+gzzXXfz+KiDN5AyE7PGkZKdYP/9Q1zm8k64XZ7t293xv3mRnQDTu" +
            "x0VMhVN8Ct6t99ncKcTq2d9LDuff8tkoxm9Mxvg5qKx7o02MkdUdwIFluNuMhO5fAh4J4T5Yfn/E4b45Gu8AZd1/FnP6UuIi6mzuFLQo01/W/SfAs2GKCXlHikmKhnVBs4gTovfdYeh3lfbI+wST2I/ligFR+6IJkBsm3dW54glAq8W+0elsKdx6y2d482K92qPM8+GKg5L5YEzmi4C/AdOKLwQdfNXJT9zEhKl72D8BzdL9bqTi4FR+G0Us4i8W85Dm4" +
            "qzNjW3M4r0Q/4q42adEPJ5VHG4kYj/luLD7AG+IOYeZzJMBJT5Os7FDUNFXU/j9J/SuFTHFScsQA0p6+0zsskPE4f0A+MfmWzwozl40SUe7cTEg7u8xNuHRG9TXbslnIsf9gHTAc2I/9b2IkR1zMdddEXFHHWI+KxZz2l0ObUZDDLzXYt4hee8E5AHyRexwmUC2QJZ0L+kUjxntJP93IZT+kucYXwzpG+r3hCfMsbpxilOc4hSnOLmm/wHgLzR/",

            // vertical
            "eNrlW2tMHFUUXt4+Yq2JUSGBlraxQEGwxVhJQDC1tFjLjzakxpQ0xJ/2EX8oRtNYq1Yb0wqBtrHQQk2F1GAhSqRBTakxGnwgkjTxh00VTcOjQngYXjvHe4Z7N6fXO7Mzs3dh0ZN8mWHY2Z3zftw7Pt+CUDQ/PsowyAAMBj9STDO86PtvkeA9juF9zuesxD+ez/HzrxmW83tiljDf9NljGR5k6LPRPb1WQuS2lOSAzxxF/k5leJyhkaGH84Z69ks8Gxzi" +
            "+mWGSoZkhjslO4qKcDsXz7iL8zwk9BoVFWXExMTMsSNER0cDuW4e2TUqG8R1hhqGXJvfijTKZ/hQsu1ZSb8mEhISID4+XvYDP8ccl8skwwjDCwwJES6DvQy/KHgx7by8vBwqKyvh4sWLcOXKFejr64Oenh5ob2+H06dPw+7duyEvL4/6w5wkmzqGhyNEBrIfvs71DMTPAz6+ceNGYGSADfn9fvN4+PBhg/uDobCbzxk2RZgdvKuw4YDP43HdunUwNDQE" +
            "hmHA3NycySseEeLa7OysyX9DQ4N5D4sVoJAn4geGpxdRBlT3b0s5zK/IbZCammryNjMzY/KrInG9urraKi9SGXy/SHZAf+clJ7wjVq9efYuNI68U9H+1tbUiH6i+i8qggyHLxifDyftOhmGVzVOI3JadnQ03btywc/+A/Z8/f968JzY2FizkSWVQL+WFhaAMhi+d6N1O/7LdO9S/SgZ7F7g2esfiOSwh+7+Kfwv/t4OQ+xivO0StHQ4S3/sU+d2gvAv7" +
            "z8jIgJs3b/6LZ3qOOQDp7NmzwexfzoszDA0MiWHW+wMMnYrYbATjPy0tDcbGxm7hWY5/gv+6ujpQ5D8rOQgd/MbwZJj8QMS9Qobfie0ZDu3UVf6rqqpyav9CBqLu+pghXrMMoogMPrHo34Pa/5o1a8z4jjEO+aT1D56Lv5FOnjzpJP6p7O8vhi2a9R/FkR5kdgPB8h/Wf06osbExmP+r+BexoIrMDHTIQcwe3lLYvWM5FBYWwuTkJHR3d0NXVxe0traafn7m" +
            "zBm4dOkS9Pb2mv0QUnNzsxv7l/MQ2sAjmv0f51G/2tU5TpCeng6JiYnK/6GuU1JSoKCgANavX+8k9ln5wN8M5ZpqYnH/Y2SO4Sf5x5Mc0K+RXwTyGRcXF/B19BfhM+LoAiIO4vzhbk22jz50UPp+V5DnPXZxwslnHdhAL6mJdcSADyXbN9zyj8dly5aZus7MzITi4mLYvn07lJSUQH5+PiQlJSnv8SCHGX5eoCn238PQL80tXQPrP5zziBgn0+DgIHR0" +
            "dMDRo0chNzfXK+/0uFfT7DhXUXe6fr6cnBwYGBgI9DkIrIVEDSBoamoKTp06ZdqJixhI+RY+2hpiDBT3FZEaG7xi1apVgR5X1f8gRP8r+j+PEHLo0lT3vey25rPjX+jarv+tqakJNQYAz9dxIcQ/cV9tKHk/TPMPJz0h8Jo11NzfrJrnuuUf639R9wfT/4kTJ5zW/3ZyAD6nCYUSeE/lmf8wzz+C5cCMEGoAof/LOuwfa9+RkRFH84/6+nov9q/KgcUa" +
            "7P+r/yn/SLeTnn8p2f80P38oBPsX97QtsfhHbTVHg/03LrH8R/WTraH+eUOqKw1d9Y/V+o+G+sfP12buIvMrr/ovC6X3lf1f1L+q2adG/8fjz5pmP1tC6X+EDrOysgLzP7HmK2aeeC56IaRz5845nX/b9T/f8N4v1P5vLd+DEVL/u2HDhoB9W5FYH9i3b58X+zekOH1IU/+L9IXT9Q47lJaWwoEDB+DChQvmDPTq1avmPKCtrc2ceW/dutVcJwnR9gX/z2ic" +
            "f3+gowaQgft/sM9XzQdd6l/Wy58MKRrmX8J2nuU+AF5zoAD6NAJ5FLM+Og/1OP+Ubf9bhvs1zr/XBlvnDzbv9SIvj/Ie4evTuvaECPtpCWX9A+dfx48fh7KyMnMtpKioCJKTk2HlypWm32/btg32798PnZ2dcOTIEa81n3ienZr3xOD37PKy7knj/8TEhHK9Uz5vampyk/9kXXRI63a69L+c1xSe1z8xx9P1X7rnC89x9uly/VO19+i1MO0DQRt4hWHc" +
            "Sy5U9X9WfaDL+o+uf//IcJ/GvC/XwthPjrqJg3b7H6z6f4f7H2T5T/A12nDonvrSq272/oRp/4uq1/3DN/9+hW7dy5TIZ+sQqv1rmH9Q+T9PbDVcNiCogmFK0RMZC7j/jfpfE1+nCzfRfHrMSU8YpvkH5f0awxMWegon3cHwKbFDw84nNOqfynuGrHPqrHec+kMaiQWzdn6gKf/J3/vmIvIu1kfyfPPvbFE7MGT7x/3vw8PDtvF/enpa7H9X2b9cd1b7" +
            "FpeiiR8UEDtQzSENnP+Pj48HeEZexTq44FuQ4J/Yv5xnj9nE5sWQwW0MmQyfWcwizPqnq6vLGB0dNX1ANfvp7++HlpYWY8eOHaqeVuzzPeiLLIomR3xX7T2LvszkB/d2YS7YvHkzVFRUwJ49e8w9XytWrDB7QVrTSj1wN59DRCLJsQf3n7VL/f8sfxfGrm72s8/62ef8nHc/1/8haS07Ut8BpHQvw3M8Nl6nPKIcOH+Gj7wjJMWMn7gtbQoi60i3hSTf" +
            "/P5BXEcZ8Nm//yr6uDouP3kP21LQO31O+i4C7sv+iNQthkUfd43n1KWibzc2UUr6Z8Oil/nON/+ewYLQP8rnNH8=",

            // chain
            "eNrtmk9qg0AYR/0HPYG602sIuvQ2XTY5Twtd9TYuBM8huLHGflVxihU7UZtE/eb3YFCySHyPkMyMahoAAAAAAAAAgDuj98NQ1H2IoZi78H1vxrk/txRzf20G9eNZgQZT7p/NuCjQYMq96o9fzBvI3Il5A6m7YRjdYNrgqrs413WdW4Oh+9vY3TTN7hgEAfm+/+s1Bg2k" +
            "7pZldccwDKksS0rTlFzX5dJglnsURZTnOQmSJCHHcY7eYLF7XddUVRWHBqvcBQdv8C/3gze4iftBG9zUXdZgh3Ok4fr9z7lN+x+3xH2qgW3bc+ZIj94/aD/vqRkfg3Xcj3t7rXEcU1EUi93HDbIsI8/zuvccNRC9Txs02JP/ywb+qn//r65vmP/+3aUBgznA6gaM5oCL" +
            "GzBcA8xuwMR9dQNG7osaMN3/mNWA+f4X9j+x/z27gYZ7QNzdZQ1Uuf8pa6CK+1QD1Z5/mNo/2Gr9vocGqj7/BAAAAAAAAAAP5xsO9WOY",

            // chainstart
            "eNrlm81O2zAcwPPRqpN22C6ll6loGtux6xEExz4Dp5Zy6+Cww6An9gTAjT4CPMMmJu0ZOFTqrsAuVammStXWjySeHfkPJmvz7Y8wS1aikNT+/WISx/5b06Qkk25f4/yC7hscy8vR7WecL5g66BLZ10zTvDUM4xvef8bRAbB/wNnBGeF8JsC5X13eYu4bWheE979ycgDl" +
            "7dOybJznHgfC2XO53DWpQ6PRsDY2Ntz65PP5tB1AeXuU12LuPzg4l8W+s7MzRzjd3d2harU6S9mBH7tD2wHZP5LB3mw2Xfb53N2gfr+fpoMgdovut2Sy27aNHMdBlmWl6SAs+57nfCnskFJykEn2lBxAeQdZZE/oAMprZ5k9poMnxR7BgflU2UM60APYoX/zSVV2cpxk" +
            "ck5EB5d4+5yy+d33Q5XZ2RTBgcuGy/rB8GaOHXjr9To6Pj5GbF8whAObOkDw/ZRF9t3d3fv6dzqdJA4cxkOm2PH/MjJNM64DRB0QF79Vf9Z52aH96rqexAG0gx6tl85xTCN1dshpOKDvBV7jSFzYDcNwcwoOLE5jKFzZ2fufgoMZBweJ2fH5/7AD4/r6OlpdXX10TCEH" +
            "XNhhf3NzE02nU9TtdlGpVFLNAVf2ra0tNBqN7q+7urpCKysrqjgQxk7OByZFHAhlh6SIAynsijiQyi7ZgakCu5+DlPpIfg7e4DJifb/79W3IOy4K+yIHxWIxsI8UdvygUqnMmPnGPGV/j3/vJznearUsuMZvzIqUN5lM0Pb29v13HMtO6lir1dB4PI7M7nXQ6/VQuVx2" +
            "f5N1AL5PTk4Cy4C/DYdD0ueaketxPb/j619mnf/09DQp/6P2D/OS/1H7N3l832Ts+ZdTwYGg91+BVx8giQPJ7FIdKMIuxYFi7MIdKMguxIGg8Y+C7DGgRQ4EjX8VVBkDlDD+WRAwBmwpNP7Nk32Rg1vaV7YVmP8Qwe518AozDMjcI3bgSJz/EsnO8n/E+Q+deyTtAEmY" +
            "/5TF3mbn3eE+xm0HGWVnY04Q4yDS8yBm/IMs9sMl8TYOdfArioOY8S+qsUPMCXkerGEHNwLin0SzQ5zVfAl7m7nmnYD4N5HsQbHEbeZ8EfGPqrKLiH9VnZ1n/LNK7Ach4qzSjH9XiT1KLHHS9Q9fMswe2oFkdkgtjjHkUdc/iWY/0h7WBvKKn1+6/m0wGMhkJ+lce7wm" +
            "kNfagaD1jzLYIZ0xDmBt4D6HeFq/9a+y2A2PA0d7WBvII5bYu/75UiI7xAxDnS60hzWhPOOoRa9/X5r+AgRFNMA=",

            // hold
            "eNrlm7vO2jAUgCEOolu7ABuoartSRhCMPAMTt413AUbepVUr9RkYkJh7WRAwIDFwCZzaEUb5gXBJYvvYsWTZCnGS7wMR+9hOJJQkcio/0vz+VLcS8Uic/TMh5K9lWT9p/V1MHNin8gvl/kNLYJnWv8fAwZndtu3fjLvVajmVSmXP6qlUymQHV+ztdnsPNC0WCyiVSjuD" +
            "HVyxdzodl32/dwuYzWamOvBlPxwOcDwewXEcUx08ZOfJQAdPsxvo4GV2gxwEZjfAQWh2jR1Exq6hg0Ds7DjL7ByNHQRm9yZNHQRm57zNZhP6/T54+4KaOAjN3u12gY//RqORTg4iY6fPD4QQnRxExk7bu2UymdTFQeTsPGvgQAi7ZVluRu5AGLv3+0fqIDQ7Pf+KnTOW" +
            "y2UoFApvjiFyIISd16vVKmy3W5hMJpDL5bA5EMpeq9VgtVqd243HY8hms1gcSGNn53MmJA6ksvOExIESdiQOlLIrdkAwsN9zIKGP9IneI9D4/V7fhr3jXmG/5SCTyTzsIz0bPygWizvPfGPqxP6VXu8fO97r9Rze5l7Mit1vs9lAo9E4j+O87OwZ6/U6rNfrl9kvHUyn" +
            "U8jn8+41vQ6478Fg8PAe/LPlcsn6XDvWnj7nL9r+g+78w+EwLP+b3z+fl4zR75+IGN9o9v9nY3Ag6f2XFtUHCONAMbtSB0jYlThAxi7dAUJ2KQ4kxT/SqmNAtxxIin+lscQAFcQ/07rEgAX0baJm12n+QxS7DvNfotkxz3/KYsc4/y2bHdP6B1XsGNa/qGZXuf4JC7uK" +
            "9W/Y2GWuf8TKLmP9K3Z2keufdWEP7MAg9pcd+LB/05j9aQcGsz904LP/ySR2Xwd8nmk+n5vO7uvgYv+jyexXDm7sfzWdPXEx38j3P/+IEfulA+X73/8DzPFTzg==",

            // melee
            "eNrVW3lwjd0ZdyMIESMVJKOko6KViXzSmKndpIMY21gGtQxtPjN2wigTmqFozfeHjvGP7QtD24xaa/tqK9oxZWiFEkrHUhVDJ6gtSG7y9PzOvM+dc0/Ou9ybm+Q6M8/c99733Pc9v2c/5zynSZOINp+gpoJiDfeaCfq2oB8KGiFoqqA8QT8RNFnQMEFZgjrYPBvPjGkS" +
            "nY1xq62VoEGCVgr6g6Dbgv4nqEYQ2VCVoHJBfxf0G0HzBGVaz+cWY3hXYzZ9LD8StF3Qvx1wggfVGjnx5ZagXwrq6cLzhmwxilziBH0p6IoBJ2Tq94CR+zNv/NZ/1fv47big4Rr/fQ2MXbXvGYJKDZirXbCGQtUGXvxJ0GAHPawvO2cf9IU1BlU2kcRspx/6e2BrSQa5" +
            "1Ie+c8sXVNGAuE3kV+zpoRVTdLuMtI9LELRPGwM1Mql2sVrRU1+EsXcR9DflnTVRgF31D6yDexQ7iIkQ9jRB963nV0YRbt038NiOWzHJVwce8P++I+iRQdeilZgH3yg5oy8MPw9KFHTzM8Ku82CPEhd8IWBnvf/mM8Su86AwxNjI2H/R0Pbu8/moadOmkmJiYiThN1CY" +
            "/oDlNsxjjsT3Byj/jwo/r/ImNjY2QMwrh7iA8f9H0LcsXxDjktvFKfmsv6Gw4TMpKYlycnJo4MCB1KtXL0pNTaU2bdpInG7PAB8c8iR8fu2iA/z7zyJl86rMnHSY8S1btozU9uHDB3r+/DndvXuXLl26RMeOHaOdO3fShg0bKD8/n6ZOnUq5ubnUqVMn+X8bPahReNDX" +
            "hgccI9oLeqHlE64YPehhEFZdVox/7ty5VFVVRRUVFVRTU0Ne26tXr2jWrFlOPGD8F2zyIvaN6yIhe8bTsmVLGjBgAPXu3ZuSk5ONvMN42ZYzMzODcFVXV1NlZaUk8EUnv98viRt0wcEWWJ5DNB3guNhW0HNl/u0qd1CXLl1o9uzZNHr0aOrWrVtQn5kzZwbG9ubNGyop" +
            "KaGtW7fS8OHDqUWLFsbnTp48mY4ePUrPnj2rJWfoBOMGb7iBP7hXXFzshJ914KSmAyz7WaH4PH5HUVFRYBwfP36kq1ev0vz58yVvMB6ME2PW27x586ht27Z07tw5OnnypOTL4sWLKSsrS+pNs2bNqGfPnrR8+XI6ceKELT/AC37+gQMH3Hwhx7R0hQfMh79ovsLVX8M3" +
            "Y1w8BrbZBw8eSPk+evQooMe4h0/wCNdDhgyhkSNHGu25vLycDh06RGPGjAl6L/ixcOFCOnz4MD19+jTITtCOHz/uhp/t+lea7L+r5Dk1XuwbPJgwYYJ8L9sg9BDX69evl7bAMlLlhQZegXcbN26U/cETtmnd740bN45atWpFI0aMoD59+gT8CPwGYuSWLVsC8ocuOfhA" +
            "1Qfc0XLi2aH4PebvwYMHA3hUPgwePJjmzJkTdE+VU2lpqfw/bEX9Xe3Hdp6SkkJTpkwJuv/kyROpA9OnT5eyGD9+vOTb5cuXg/TTZY0xU/H/v/eKn3kLOcCnsVxZbu/evZOyhS6qPFF5sWPHDoqPj5fxXdcRlR9lZWXyXeiP53z69KmWrezfv1/2gQ4xX114wBgXWNhb" +
            "CLqn6YdrbIPPN8n+7Nmz1Lp1a3r9+nUtbNx32rRp1LdvX6Ps1WfBL+JdN27cCPyu+hE8DzbUsWNHef/OnTsB+XjA/zsLf3dBH7zaPj///PnzQWNlbEuWLLHFxryAbygoKKhlHzqf1qxZI20fWO14OXHiRJkvo927dy8QVx3ws4xLLN8/OlTsaWlpUhfV8TBW+GiMm2O1" +
            "jh2+HbENstXtQ5f/sGHDqF+/fo68TE9Pp0WLFsnrhw8fSn654Gec5dZe22KvcZ91f+XKlUEy4LEhV8f7r1y5UgsbX586dYqaN29OL168MNq+ig96jfiv6wn/B3ky3rd79+7A+5FTePCBbAfYb/zKK36ez1y/fj0IN49t79691KFDh8B3k76uWrWKevToYYtd932Y8+i8" +
            "5D63bt2Sfa5duya/w+cgXniMAfjMFbTDC36OecjlVX+vYkM8Qs5i0mv+PmjQIDlXsbN97oecDxg4z1H1n/vs27eP4uLiAnHo/fv3cs7skgOoPuDHgn7rBT/rPvytnT527dqVtm/fbnsf+VG7du1kXmxn+/y/wsJC6ty5s1FPuM+KFSukL1LnAfgeAv6fWnHAET/PdZCX" +
            "w8eo8uBPxCj0Re6ry4uvb968KeWl5sV28kd+zPmlrifcZ+jQoTR27NhAH1BGRkYo+FF/sNMNP+u+Ka6xbB4/fky7du0y2rSa90A/3eb2uI/8avPmzbZ2ggb9WLt2bSAvQMvOznabA6j4pwn6tRt+1n3OQ01669R4/Mjlec5jwsR8BS8RI035MeNE/MC4jhw5Ir+/fftW" +
            "5hSIB7xu6sH/jRS03Ct+zFntxq7He1OsRn6uxw6TXiN3TkxMlPFNt3/+38WLFyXOly9f0unTp2UeEMoaDfSjffv2/cX1JLfcl/EvXbpUjhF+pj4a82/BggXSjk26xn0w7wOP8vLyao3Tw7o4cpCKnJyc74nrXm7zHn7ujBkzAmNQ5/ReG/o69ed7WMdALmHSE8YPWejr" +
            "aCHsleLzn1ZNVoK1Pm6bA7P/T0hIoG3btlFjNuYH1tAsOYZTN0BWTRa3P4ay9oW1COReyDvgqyLd9HVN3Y9g7oFcw0Occ5r/rVLwF3iZ/3P+i2vMs7DujpwAft20dlNfskcOwnIPY2+M9X+ggj9bWRcJa68FeTqvV9dXY52Avw9T9oy9zKpP5PXvWGXPy/O+B+99YByj" +
            "Ro0KGqe6Pu/m90KJD9CxTZs2heLvTbpfpKx/8hpoYbh7H2wX2JvidZ9w9dvJhlj+mGeFiZ9lO1jZA+H171SrpqumLnu+8AmTJk2SuTDiGNa64Ktv377tGC9N8wUTb7Dejjl+GPviXC9WYqiZ5L2g3XXZ/3Kyx/79+wd8t5o/qDy5f/++zGPV+QzbD/sV7HuGKXu/MufT" +
            "6yF4/zPd2geoDlcH1D1f3qtHLo97Z86csdXtdevWyZjSvXt3unDhgrEP1jng9z3k93Z1AP9S6qJ8Nvvf2yK998+6iv197N9g/o+1WsgU+oB9L7U/+AAbxz43bAj7hsg3sG5Ux5g/xaEGgHUgOdQ98HAIugE8jIlrBOywsb6HEe9Zjn/2UAPD9/Lqo+5JrRVw8hvcT+8b" +
            "htx5P/OjsufptQboYH3WfrGsvWCqQw0Ujz0/hBpxrgNKtPxFtNT5hlz/Bp7Fx8cfC6M+Xq1xf+O1JiKKqMqqPyldvXp1skvdl9sZh1ylDq76c8Fu1cR3rmMdNPNgrFIf4P9MsKdF6FwI8wBnbl5HcT0sj+kfVs12JM/EMA+ylL3yaKkNVfOUE1adZ32cB2IeJCn1Eo3J" +
            "B7W+t8aqV/ZF6NyDl7N+X1r1cqbzOA2FG3RNmc9G8tyLlzNgnQRtEfRJ04fqesCsnwf8r7V3EdeA59/sdOELEWuLxTyv0uCT/GGsKRjPQFp5YJlVp5rSwGf/HM9KiDy2SWpq6vfF5c8tH2znq/wuZOJTleDvXzMyMgpSUlKSGvnsp12+GKPxJdvST/jkxyHGzAqrRq/Y" +
            "qkvtLuTeJApxm/hgOmeCPZYfCJooaIW151pk1R7ssc5wfmXV4qAeCXrU3KBrsZHG/X9AzmJS",

            // mine
            "eNrtW1tIVUsYdnvv+BJm4t1CsAcR2up5NSSyQwbRm0SBJseejj34IFl4OQqGT3JMM9M3pQcL4mgQ9FCdvIWgKBwKtLAXK+yyveFlb/ec+Yf5N+Nq7e3aM7P2Do4DP0sXa6/1f/9t/vnnn4gIbSOSUrThXgwlJ6XfKfVS+ofSO0rfKbkpeTi5+b13/Jle/hsnf4c4ovm3" +
            "fpbhoBQl/B9P6TdK3ZT+pbRDiUjSDn9HN39nvPCdKP7tcOIW9X2M0p+UFkxw7HI9w9XLyfgM3hefNT6zwL9xzGAPoZaDaH9ZlP6itGLA6xbwyurfa3gX3l/h38zyw5OdA3UeS6mO0leBL7cfvekilAX+/5XzEGvgzW4//5XSlAG3tJ4dDoeMXYhymOI82RUXIoV31lDa" +
            "0oFbA4ly2OK8oa4cmn0d5Hpf+LZHB4b4+HiSk5MjawdmvPQKdhqpCXsCpWGdOkesx48fJ83NzezvyMhIXbbwN+dZRQZo8/CeF8J8rMV2o6Ki2LW0tJRMTEyo6t+YMxDO8y+SvoCxDmhEN3ag6Ohodq2rqyObm5skISHBDhmMCDgcEnNcnx3YRf0/evSIwMjPz9fhA2Yy" +
            "6AtybowS4rwt2AEj0KFDh8j8/DzDf+nSJXYP7UKzDP4wYNsPewGlbR5XvbowAzbRvnNzc4nb7Wb4b9++7bMLJA2+4OUYtjmmQDJAn4dcakbXHGeGIzU1lZw8eZK0tbURHK9evSKZmZk/2D/6iQIhhhmOzV8sQLnUCfOc0vwmYikqKiItLS1kfHycfPv2jZiN9fV1Mjc3" +
            "R7q7u8mZM2f2YFeMC4ilzo8N4FyXRsmlum4R+QYcz549M8Xr9XpJoDEzM0OuXr3qsx8FW8B1lItjdBjyApTHHVXdI49g44ODg3vwgK97PB6/uOH+7u4uew6uOEZHR4nT6VSVAWK6Y8CMcsiktB5gfW55Ti8uLiaLi4uMd8ABmGWG+FvIEaqqqvZ8R8IGvBxjpknNqllF" +
            "96iX8+fPk+3tbZ++dQxRfvX19SoyQGzNhtoJ1JUWhTW2FPZTp075sMvq3N8A30B51tTUyPoCYlvkmHEeOCuLHeIyxKf09HTy6dMnW7CLMsB3nz59WlUGZ4X412NSU7CMH64jIyNabT5QTIDx4cMHcvjwYZ/8g/QBL8eMdaw3MvpH2V+4cCEk2MV5xJgzSuj/DcfulM3x" +
            "Qe4Qh6anp/fYpt0D58m1tTXmd5L50Q7HXq2ie/BD0S5DNdAGbty4ITMfINZqXi8KOtdH/H19fXtic6gG5lGQI0rEAI9QK3sZrP4xt4fa3cLCQlj0jzkkyP3EiRPB+gBiBezvhfwoIF6wMVybwv3s7Gyf3vfL5e2yAawdAG9xcXFWbQGxvudrgqBjH+T3tbW1YdG9MQY0" +
            "NTWZ+qeFGsL3QHM+/DYmJoYkJSWx9WtlZSW5e/cuef36NVlZWSHhHmhzHz9+JD09PeTy5cusphJELHCbxT34PVBaWhq5desWmZqaCpiL/GwD8tChoSGmryNHjgSqq3r2i/vgV4mJiaxeU1FRwWoTk5OTfmsY4dA/2OLY2Bhbb7e2tpJr166Rc+fOMZ6xrhxgHpBa76Wk" +
            "pJCbN2/+FP7f2dmpUhNwWZ3vjPG/oKAgbLFfxA/rQeAR4j/wiHxamA+/W5n//K35kpOTicvlCpsM8JslJSXBrgPE+e+lTP6Lsn3+/HlIc38j9s+fP7N1YJB7R2L+I5X/Yr59/fr1kK79RNsHGTx48EBmDSjmv9Uq+oc5cnV1lfESSh/AmAv7pwpr4GqV9S9+s6OjI6Q2" +
            "gL4GeRjupUnujTlV6h8YB2Eu/PLlC9NJKOZClDPUGzXUP5TqX/jtK1euMJ52dnZsxY7v7+rqkq3/GetfSvVPkYd79+7ZKgN8L9g97B1L7pEa65/K9W/gAfOix48f+3jVGQ8R+9u3b9naU7LmZVb/1rL/gWsmWC8ODAyY7l2o7gGB3jMyMlT2Q832P7Ttf6EMsK9la2tr" +
            "Dwar9oD5lCi7/v5+31pGEru//S+t+5/ivndhYSF5+vTpD/rEfVAjXuN9GFBbhvq6hn1wf/uf2ve/jXEZasSQpy0vL1vS/8bGBpNbeXm5L8+UqHEGs/+tvf/BjOejR4+SsrIy0tDQQJ48eeLDC+t3qFe0t7czzNATqLkHZL/+B9v6X8RanHgP4uTS0hLD//DhQ79zioYe" +
            "IKv9L7b2P4mYYmNj2d/Dw8MMf2NjI7sP9XSwd409cMH0P4Ws/03sfYRx8eJFlT19K/1vNRaxG/sf++2QAfoC9InAyMvL0937qNL/aHv/K/o0xMPZ2VlWX9fY+6qj/9X2/mfUN+xdQN3OBr2/4Lw7FHvAbel/R8rKytJl9+L6dVhD/3tIzj9oIpGX+xrPP9h+/kXR5wOd" +
            "f9F9Hsy280+KuENx/ung/NvB+ceD86/7x4WIiP/P+Wczn7B6/t0l7L0juSLCcP79P+F8xV4=",

        };

        private static readonly int iconSize = 64;
        private static readonly int atlasColumns = 4;

        private static byte[] DecompressZlib(byte[] compressed)
        {
            // Skip zlib header (2 bytes) to get raw deflate stream
            using (var input = new MemoryStream(compressed, 2, compressed.Length - 2))
            using (var deflate = new DeflateStream(input, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                deflate.CopyTo(output);
                return output.ToArray();
            }
        }

        public static void InitializeTargetIcons()
        {
            if (targetIconsInitialized) return;

            try
            {
                int iconCount = iconNames.Length;
                int atlasRows = (iconCount + atlasColumns - 1) / atlasColumns;
                int atlasWidth = atlasColumns * iconSize;
                int atlasHeight = atlasRows * iconSize;

                // Create atlas texture
                Texture2D atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false);
                atlas.name = "TargetIconsAtlas";
                atlas.hideFlags = HideFlags.DontUnloadUnusedAsset;

                // Clear atlas to transparent
                Il2CppStructArray<Color> clearPixels = new Il2CppStructArray<Color>(atlasWidth * atlasHeight);
                Color transparent = new Color(0, 0, 0, 0);
                for (int i = 0; i < clearPixels.Length; i++)
                    clearPixels[i] = transparent;
                atlas.SetPixels(clearPixels);

                // Decode and place each icon
                for (int i = 0; i < iconCount; i++)
                {
                    byte[] compressed = Convert.FromBase64String(iconBase64[i]);
                    byte[] rawRGBA = DecompressZlib(compressed);

                    int col = i % atlasColumns;
                    int row = atlasRows - 1 - (i / atlasColumns);

                    // Convert raw RGBA bytes to Color array and set directly on atlas
                    Il2CppStructArray<Color> iconPixels = new Il2CppStructArray<Color>(iconSize * iconSize);
                    for (int j = 0; j < iconSize * iconSize; j++)
                    {
                        int idx = j * 4;
                        iconPixels[j] = new Color(
                            rawRGBA[idx] / 255f,
                            rawRGBA[idx + 1] / 255f,
                            rawRGBA[idx + 2] / 255f,
                            rawRGBA[idx + 3] / 255f
                        );
                    }

                    atlas.SetPixels(col * iconSize, row * iconSize, iconSize, iconSize, iconPixels);
                }

                atlas.Apply();

                // Create material
                Material mat = new Material(Shader.Find("Sprites/Default"));
                mat.name = "TargetIconsMaterial";
                mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
                mat.mainTexture = atlas;

                // Create TMP_SpriteAsset
                targetIconsSpriteAsset = ScriptableObject.CreateInstance<TMP_SpriteAsset>();
                targetIconsSpriteAsset.name = "TargetIcons";
                targetIconsSpriteAsset.hideFlags = HideFlags.DontUnloadUnusedAsset;
                targetIconsSpriteAsset.spriteSheet = atlas;
                targetIconsSpriteAsset.material = mat;
                targetIconsSpriteAsset.hashCode = TMP_TextUtilities.GetSimpleHashCode("TargetIcons");
                targetIconsSpriteAsset.materialHashCode = TMP_TextUtilities.GetSimpleHashCode("TargetIconsMaterial");

                // Build spriteInfoList (legacy format required by UpdateLookupTables)
                var spriteInfoList = new Il2CppSystem.Collections.Generic.List<TMP_Sprite>();

                for (int i = 0; i < iconCount; i++)
                {
                    int col = i % atlasColumns;
                    int row = atlasRows - 1 - (i / atlasColumns);

                    int x = col * iconSize;
                    int y = row * iconSize;

                    TMP_Sprite sprite = new TMP_Sprite();
                    sprite.name = iconNames[i];
                    sprite.id = i;
                    sprite.x = x;
                    sprite.y = y;
                    sprite.width = iconSize;
                    sprite.height = iconSize;
                    sprite.xOffset = 0;
                    sprite.yOffset = iconSize * 0.9f;
                    sprite.xAdvance = iconSize;
                    sprite.scale = 1f;
                    sprite.hashCode = TMP_TextUtilities.GetSimpleHashCode(iconNames[i]);
                    sprite.unicode = 0xFE00 + i;

                    spriteInfoList.Add(sprite);
                }

                targetIconsSpriteAsset.spriteInfoList = spriteInfoList;

                // Let TMP build the glyph and character tables from spriteInfoList
                targetIconsSpriteAsset.UpdateLookupTables();

                // Register in MaterialReferenceManager so TMP can find it by name
                MaterialReferenceManager.AddSpriteAsset(targetIconsSpriteAsset.hashCode, targetIconsSpriteAsset);

                // Register as fallback on the default sprite asset
                var defaultAsset = TMP_Settings.instance.m_defaultSpriteAsset;
                if (defaultAsset != null)
                {
                    if (defaultAsset.fallbackSpriteAssets == null)
                    {
                        defaultAsset.fallbackSpriteAssets = new Il2CppSystem.Collections.Generic.List<TMP_SpriteAsset>();
                    }

                    bool alreadyRegistered = false;
                    for (int i = 0; i < defaultAsset.fallbackSpriteAssets.Count; i++)
                    {
                        if (defaultAsset.fallbackSpriteAssets[i].name == "TargetIcons")
                        {
                            alreadyRegistered = true;
                            break;
                        }
                    }

                    if (!alreadyRegistered)
                    {
                        defaultAsset.fallbackSpriteAssets.Add(targetIconsSpriteAsset);
                        MelonLogger.Log("TargetIcons sprite asset registered as fallback");
                    }
                }

                targetIconsInitialized = true;
                MelonLogger.Log($"TargetIcons initialized: {iconCount} icons in {atlasWidth}x{atlasHeight} atlas");
            }
            catch (Exception ex)
            {
                MelonLogger.LogError($"Failed to initialize TargetIcons: {ex.Message}");
                MelonLogger.LogError(ex.StackTrace);
            }
        }

        public static void CleanupTargetIcons()
        {
            if (targetIconsSpriteAsset != null)
            {
                var defaultAsset = TMP_Settings.instance?.m_defaultSpriteAsset;
                if (defaultAsset?.fallbackSpriteAssets != null)
                {
                    for (int i = defaultAsset.fallbackSpriteAssets.Count - 1; i >= 0; i--)
                    {
                        if (defaultAsset.fallbackSpriteAssets[i].name == "TargetIcons")
                        {
                            defaultAsset.fallbackSpriteAssets.RemoveAt(i);
                        }
                    }
                }

                targetIconsSpriteAsset = null;
            }
            targetIconsInitialized = false;
        }

        /// <summary>
        /// Returns a TMP rich text sprite tag for the given target icon name.
        /// Valid names: standard, horizontal, chain, chainstart, hold, melee, mine
        /// </summary>
        public static string GetTargetIconTag(string iconName)
        {
            return $"<sprite=\"TargetIcons\" name=\"{iconName}\" tint=1>";
        }

        public static string GetColoredTargetIconTag(string iconName, Color color)
        {
            string hex = ColorUtility.ToHtmlStringRGB(color);
            return $"<color=#{hex}>{GetTargetIconTag(iconName)}</color>";
        }
    }
}